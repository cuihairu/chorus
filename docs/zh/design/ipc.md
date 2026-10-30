---
title: 通信抽象（进程内 / 进程间）
---


# 通信抽象（进程内 / 进程间）

你提到的思路是对的：把"进程内通信 / 进程间通信"统一封装为单一 `channel`（消息队列），在其上提供 Actor（单线程信箱）模型，让上层只面对 `Tell/Publish`，从而屏蔽底层细节。

## 统一抽象

- **Channel（消息投递）**：负责把 `Envelope` 投递到某个"目的地"
- **Actor（有序处理）**：每个 `sessionId` 对应一个 actor，保证同一会话的消息被串行处理
- **Router（路由）**：决定同一 `sessionId` 的消息应落到哪个 actor / 哪个进程

## 语义差异（必须显式处理）

即便抽象统一，进程间通信仍带来无法回避的语义差异：

- **投递语义**：IPC/网络一般是 at-least-once（可能重复）→ 必须幂等/去重
- **顺序**：依赖 `orderingKey=sessionId` 做分区/路由，否则消息会乱序
- **背压**：队列积压时必须有限流/降采样/快照追赶策略

## 传输层优先级（localhost 优先）

按"实现复杂度 vs 性能收益"分层（你给的顺序也合理）：

1. **进程内队列（Channel）**：最快最可靠（Monolith 默认）
2. **本机 IPC（Unix Domain Socket / NamedPipe）**：跨进程但同机，运维简单
3. **localhost TCP**：更通用的跨进程/跨容器方案（拆容器/多机前的过渡方案）
4. **远程 TCP/消息队列**：多机部署
5. **共享内存（MemoryMappedFile + RingBuffer + FlatBuffers）**：吞吐最高，但实现与调试成本最大（仅在明确的性能瓶颈后引入；详细评估与触发条件见[共享内存 IPC 传输评估](./shared-memory-transport.md)）

## 仓库现状

- 单体（`InProc`）仍是默认部署形态；分离模式（Gateway + EngineHost）已实现可用（见[分离模式](../guide/split-mode.md)）
- `Chorus.Messaging` 是通道抽象与传输层：
  - `InProcMessageBus`：进程内队列
  - `NamedPipeMessageBus`：本机进程间 IPC（Windows 友好）
  - `UnixDomainSocketMessageBus`：本机进程间 IPC（Linux/macOS；较新的 Windows 也支持 AF_UNIX）
  - `TcpMessageBus`：localhost TCP（跨容器/跨网络命名空间）
  - `HttpMessageBus`：HTTP 单向投递（远程网关层/反向代理之后）
  - `FanOutMessageBus`：发布侧扇出包装，逐个发布到 N 个子总线，单个失败不阻塞其余，失败聚合后抛出

### 通道命名与多 Gateway

`IpcEndpointBuilder.Build` 生成一对通道：

- gw→eng（命令）：`{prefix}_gw_to_eng.sock`（UDS）/ `{prefix}.gw_to_eng`（NamedPipe），不按 gateway 区分（所有 gateway 共享一个 worker 的通道，引擎侧单服务器作为汇聚点），但按 worker 区分：EngineHost 设置 `Ipc:WorkerId` 给自己的通道名加后缀（`{prefix}_gw_to_eng_{workerId}.sock` / `{prefix}.gw_to_eng.{workerId}`；为空则保留旧的单 worker 名），这样一个 gateway 可以把房间路由到多个 worker
- eng→gw（快照/welcome/心跳）：每个 gateway 一个。Gateway 用 `Gateway:Id` 后缀配置自己的通道（`{prefix}_eng_to_gw_{id}.sock` / `{prefix}.eng_to_gw.{id}`）；id 为空则用旧名（`{prefix}_eng_to_gw.sock`）。EngineHost 配置 `Ipc:GatewayIds` 列出所有 gateway id，为每个 id 的通道建一个客户端并扇出
- eng→gw 分片（`Ipc:EngToGwShards > 1`，`ShardedMessageBus` / `FanInMessageBus`）：每个 gateway 的通道拆成 `{prefix}_eng_to_gw_{id}_{k}.sock` / `{prefix}.eng_to_gw.{id}.{k}`，分片索引 `k` 属于 `[0, EngToGwShards)`。引擎每帧恰好发布到一个分片，由 session id 的稳定 FNV-1a 哈希选出，因此一个会话始终走同一条 socket、会话内顺序不受影响，而不同会话不再在同一条 socket 的写入上排队。gateway 每个分片绑一个服务器，`FanInMessageBus` 把 K 路流合并进单个分发循环，保持今天的单消费者语义。计数为 `1`（默认）时逐字节产出上面无后缀的名字；分片索引是内部构建参数而非配置键，非正计数或越界索引在端点构造时即快速失败

遗留静态 IPC 通道不按 gateway/worker id 区分 TCP/HTTP 端口与 URL；多个本地 gateway 共享一个 worker 应使用 UDS/NamedPipe。在 ControlHost 模式下，每个 Worker 上报其通告端点，Gateway 从路由快照构建所选 worker 的通道，含 TCP/HTTP 端点。该路径已接通；独立主机网络验收仍 open（见[进程拓扑](./process-topology.md) §4）。

### Socket 文件生命周期（2026-10-16 决策）

UDS socket 文件是 bind 创建的路径项，此前其删除权归属引发竞态：自 .NET 6 起，`Socket.Dispose` 无条件 unlink 用 `UnixDomainSocketEndPoint` 绑定的路径（dotnet/runtime#52103，无关闭选项）。滚动重启时新进程先 delete-bind 抢占同名路径，旧进程随后 dispose——它按**路径** unlink，切断了新进程 socket 的文件项：内核 socket 没死，但后续所有连接都得 ENOENT（此前压测靠手工 `rm -f /tmp/chorus/*.sock` 规避，这就是根因）。

决策（委托决策）：`UnixDomainSocketMessageBus` 用自定义 `RawUnixEndPoint` 做服务端 bind（自管 sockaddr_un 序列化，布局与 `UnixDomainSocketEndPoint` 完全一致）。BCL 的删除追踪只识别 `UnixDomainSocketEndPoint` 类型本身（sealed），自定义端点不进入追踪，路径删除权回到 bus 自身；关闭时在 **bind 身份守卫**下删除——bind 时记录路径上文件的 `CreationTimeUtc`（Unix 上为 s_ctime，bind 后元数据不再变化），仅当文件仍是本进程绑定的那个才删除；新进程重绑的同名文件绝不可被误删。删除动作挂在 `DisposeAsync` 上（accept 循环尾巴不可靠：带取消令牌的 `Task.Run` 任务可能永远得不到调度）。异常退出的残留文件由下一次 `StartServer` 的 delete-bind 流程兜住，身份未知时保守不删。客户端的 `ConnectAsync` 保留 BCL 端点（从不 bind，不进入删除追踪）。

## 关于 Windows 的"Unix Socket 支持"

Windows 的 AF_UNIX（Unix Domain Socket）支持随系统版本与运行环境（如容器、权限、路径规则）而异；因此在框架层面建议：

- 默认策略：**只要运行环境支持 UDS，就用 UDS**
- 仅在以下情况降级：运行环境不支持，或 UDS bind/connect 失败（如旧系统、路径/权限限制）
  - 降级到：NamedPipe（Windows 上原生成熟）

## 传输选择与配置

在 `Ipc:Transport` 上提供手动选择，以兼容"单进程/拆进程/拆容器"的不同部署形态：

- `Auto`：优先 UDS（可用则用），失败/不支持时降级到 NamedPipe
- `UnixDomainSocket`：强制 UDS
- `NamedPipe`：强制 NamedPipe
- `Tcp`：强制 localhost TCP（需固定端口配置，适合拆容器或需要显式端口映射的场景）
- `Http`：强制 HTTP 单向投递（更适合远程网关层、反向代理、简单的跨主机连通）

TCP 模式新增配置项（两端必须一致）：

- `Ipc:TcpHost`：默认 `127.0.0.1`
- `Ipc:TcpGatewayToEnginePort`：Gateway -> Engine（命令通道）监听端口，默认 `27500`
- `Ipc:TcpEngineToGatewayPort`：Engine -> Gateway（事件/快照通道）监听端口，默认 `27501`

HTTP 模式新增配置项（两端必须一致）：

- `Ipc:HttpGatewayToEngineUrl`：Gateway -> Engine（命令通道）接收地址，默认 `http://127.0.0.1:27600/gw-to-eng/`
- `Ipc:HttpEngineToGatewayUrl`：Engine -> Gateway（事件/快照通道）接收地址，默认 `http://127.0.0.1:27601/eng-to-gw/`

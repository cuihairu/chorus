---
title: 通信抽象（进程内/进程间）
---

# 通信抽象（进程内/进程间）

你提到的思路是正确的：把“进程内通信/进程间通信”统一封装为一个 `channel`（消息队列），在此之上提供 Actor（单线程邮箱）模型，上层只面对 `Tell/Publish`，从而屏蔽底层细节。

## 统一抽象

- **Channel（消息投递）**：负责把 `Envelope` 投递到某个“目的地”
- **Actor（顺序处理）**：每个 `sessionId` 对应一个 actor，保证同一 session 的消息串行处理
- **Router（路由）**：决定同一个 `sessionId` 的消息应落到哪个 actor/哪个进程

## 语义差异（必须显式处理）

即便抽象统一，进程间通信也会带来不可避免的语义差异：

- **投递语义**：IPC/网络一般是 at-least-once（可能重复）→ 必须幂等/去重
- **有序性**：要靠 `orderingKey=sessionId` 做分区/路由，否则会乱序
- **背压**：队列积压时，必须有限流/降采样/快照追赶策略

## 传输层优先级（本机优先）

按“实现复杂度 vs 性能收益”建议分层（你提到的顺序也合理）：

1. **进程内队列（Channel）**：最快、最可靠（Monolith 默认）
2. **本机 IPC（Unix Domain Socket / NamedPipe）**：跨进程但仍是本机，运维简单
3. **localhost TCP**：跨进程/跨容器更通用（拆分到容器/多主机前的过渡方案）
4. **远程 TCP/消息队列**：多机部署
5. **共享内存（MemoryMappedFile + RingBuffer + FlatBuffers）**：吞吐最高，但实现与调试成本最大（建议在有明确性能瓶颈后再上；详细评估与触发条件见 [共享内存 IPC 传输评估](./shared-memory-transport.md)）

## 当前仓库状态

- 单体（`InProc`）仍是默认部署形态；拆分模式（Gateway + EngineHost）已实现可用（见 [拆分模式](../guide/split-mode.md)）
- `Chorus.Messaging` 是 channel 抽象与 transport 层：
  - `InProcMessageBus`：进程内队列
  - `NamedPipeMessageBus`：本机进程间 IPC（Windows 友好）
  - `UnixDomainSocketMessageBus`：本机进程间 IPC（Linux/macOS，Windows 较新版本支持 AF_UNIX）
  - `TcpMessageBus`：localhost TCP（跨容器/跨网络命名空间）
  - `HttpMessageBus`：HTTP 单向投递（远程网关层/反向代理后）
  - `FanOutMessageBus`：发布侧扇出包装，对 N 个子 bus 逐条发布，单条失败不阻断其余，失败聚合抛出

### 通道命名与多网关

`IpcEndpointBuilder.Build` 生成一对通道：

- gw→eng（命令）：`{prefix}_gw_to_eng.sock`（UDS）/ `{prefix}.gw_to_eng`（NamedPipe），不按网关区分（同一 worker 的通道由所有网关共享，引擎侧单 server 收口），但按 worker 区分：EngineHost 配置 `Ipc:WorkerId` 后缀出自己的通道名（`{prefix}_gw_to_eng_{workerId}.sock` / `{prefix}.gw_to_eng.{workerId}`；空 = 旧单 worker 通道名），使一个 Gateway 可把房间路由到多个 worker
- eng→gw（快照/欢迎/心跳）：每个网关各一条。Gateway 配 `Gateway:Id` 后缀自己的通道（`{prefix}_eng_to_gw_{id}.sock` / `{prefix}.eng_to_gw.{id}`），id 为空则用旧名（`{prefix}_eng_to_gw.sock`）。EngineHost 配 `Ipc:GatewayIds` 列出全部网关 id，对每个 id 的通道各建一条客户端并扇出

旧式静态 IPC 通道的 TCP/HTTP 端口与 URL 不按网关/Worker id 区分；同机多个网关共享 Worker 时应使用 UDS/NamedPipe。ControlHost 模式下，每个 Worker 上报 advertised endpoint，Gateway 根据路由快照动态建立目标 Worker 通道，也支持 TCP/HTTP。该路径已接线，独立主机网络验收仍未完成（见[进程拓扑](./process-topology.md) §4）。

### socket 文件生命周期（2026-10-16 决策）

UDS 的 socket 文件是 bind 创建的路径入口，其删除权归属曾经踩过竞态：.NET 6 起 `Socket.Dispose` 会无条件 unlink 用 `UnixDomainSocketEndPoint` 绑定的路径（dotnet/runtime#52103，无 opt-out）。滚动重启时新进程先删-绑抢走同名路径，旧进程随后 dispose——它按**路径** unlink，砍掉的是新进程 socket 的文件入口：内核 socket 未死，但后续全部连接 ENOENT（此前压测靠手工 `rm -f /tmp/chorus/*.sock` 绕过，根因即此）。

决策（已代决）：`UnixDomainSocketMessageBus` 用自定义 `RawUnixEndPoint`（自管 sockaddr_un 序列化，布局同 `UnixDomainSocketEndPoint`）做服务端 bind。BCL 的删除跟踪只识别 `UnixDomainSocketEndPoint` 本类型（sealed），自定义端点不进入跟踪，路径删除权回归总线自身；关闭时按 **bind 身份守卫**删除——记录 bind 时刻路径上文件的 `CreationTimeUtc`（Unix 上即 st_ctime，bind 后元数据不再变化），仅当文件仍是本进程 bind 时那份才删，新进程重绑的同名文件绝不误删。删除动作挂在 `DisposeAsync`（accept 循环尾部不可靠：带取消 token 的 `Task.Run` 任务可能从未被调度）。异常退出的残留文件由下次 `StartServer` 的删-绑流程兜底，身份未知时保守不删。客户端 `ConnectAsync` 保持 BCL 端点（从不 bind，不进删除跟踪）。

## 关于 Windows “支持 Unix Socket”

Windows 的 AF_UNIX（Unix Domain Socket）支持在系统版本与运行环境（例如容器、权限、路径规则）上仍有差异；因此框架层面建议：

- 默认策略：**只要运行环境支持 UDS，就用 UDS**
- 仅在以下情况降级：运行环境不支持或 UDS 绑定/连接失败（例如老系统、路径/权限限制）
  - 降级到：NamedPipe（Windows 原生且成熟）

## Transport 选择与配置

在 `Ipc:Transport` 上提供手动选择，以便兼容“单进程/拆分进程/拆分容器”的不同部署形态：

- `Auto`：优先 UDS（可用则用），失败/不支持时降级到 NamedPipe
- `UnixDomainSocket`：强制 UDS
- `NamedPipe`：强制 NamedPipe
- `Tcp`：强制 localhost TCP（需要固定端口配置，适合拆分到容器或需要显式端口映射的场景）
- `Http`：强制 HTTP 单向投递（更适合远程网关层、反向代理、跨主机简单联通）

TCP 模式新增配置项（两端都要一致）：

- `Ipc:TcpHost`：默认 `127.0.0.1`
- `Ipc:TcpGatewayToEnginePort`：Gateway -> Engine（命令通道）监听端口，默认 `27500`
- `Ipc:TcpEngineToGatewayPort`：Engine -> Gateway（事件/快照通道）监听端口，默认 `27501`

HTTP 模式新增配置项（两端都要一致）：

- `Ipc:HttpGatewayToEngineUrl`：Gateway -> Engine（命令通道）接收地址，默认 `http://127.0.0.1:27600/gw-to-eng/`
- `Ipc:HttpEngineToGatewayUrl`：Engine -> Gateway（事件/快照通道）接收地址，默认 `http://127.0.0.1:27601/eng-to-gw/`

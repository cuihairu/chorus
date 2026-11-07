---
title: 源码分层解说
---

# 源码分层解说

本页按职责归纳源码，不按目录名讲故事。仓库同时存在重设计路径与兼容装配，因此目录名会有重叠。术语定义见[源码分层术语表](../reference/source-layer-glossary.md)，英文镜像见 [Source Layer Map](../../architecture/source-layers.md)。

## 边界接入层

- **实现：** `src/Chorus.PlatformGateway/Program.cs:121` 组装平台模块，`src/Chorus.PlatformGateway/PlatformIngressEndpoint.cs:31` 暴露回调面；`src/Chorus.StreamerGateway/Program.cs:54` 构建主播 WSS 边缘，`src/Chorus.StreamerGateway/EdgeWs.cs:22` 处理公开协议。
- **配置 schema：** 平台选择位于 `src/Chorus.PlatformGateway/PlatformModules/PlatformModuleAssembly.cs:20` 的 `Ingress:Platforms`；WSS 参数解析在 `src/Chorus.StreamerGateway/EdgeOptions.cs:12`。
- **专属协议文件：** `src/Chorus.StreamerGateway/Messages/EdgeTypes.cs:9`、`src/Chorus.StreamerGateway/Messages/EdgeMessages.cs:7`、`src/Chorus.Rpc/Protos/m4.proto:13`。
- **文案挂载：** 边缘错误文案在 `src/Chorus.StreamerGateway/Messages/EdgeTypes.cs:63`；协议语义文档在 `docs/reference/protocol.md:1`。

## 业务协调层

- **实现：** `src/Chorus.ControlServer/Program.cs:56` 装配控制存储与协调服务，`src/Chorus.ControlServer/MatchCoordinatorService.cs:34` 是基于任期的匹配状态机；`src/Chorus.StreamerServer/Program.cs:132` 装配登录协调器与 Actor 注册表。
- **配置 schema：** 控制面节奏来自 `src/Chorus.ControlServer/Program.cs:110` 的环境解析；主播租户映射与平台凭证解析在 `src/Chorus.StreamerServer/Program.cs:37`。
- **专属协议文件：** `src/Chorus.Rpc/Protos/m1.proto:437` 定义主播身份/会话面，`src/Chorus.Rpc/Protos/m3.proto:96` 定义匹配、路由、部署、接管和结束面。
- **文案挂载：** 管理 RPC 文案由 `src/Chorus.ControlServer/AdminGrpcService.cs:34` 承载；架构叙事在 `docs/architecture/admin-history.md:1`。

## 对局运行层

- **实现：** `src/Chorus.GameServer/RoomRuntime.cs:18` 拥有房间生命周期与恢复；`src/Chorus.GameServer/RoomActor.cs:160` 运行单个串行 tick 执行器；`src/Chorus.GameServer/Program.cs:116` 映射运行时 RPC 面。
- **配置 schema：** 宿主上限解析在 `src/Chorus.GameServer/Program.cs:51`；模块包与输入翻译解析在 `src/Chorus.GameServer/Program.cs:146` 和 `src/Chorus.GameServer/Program.cs:156`。
- **专属协议文件：** `src/Chorus.Rpc/Protos/m2.proto:609` 定义 `GameHost`；`src/Chorus.Rpc/Protos/m2.proto:737` 定义 `RoundSync`；`src/Chorus.Rpc/Protos/m2.proto:701` 定义房间事件投递。
- **文案挂载：** 房间状态与停止原因在 `src/Chorus.GameServer/RoomActor.cs:16`；恢复叙事在 `docs/design/room-recovery.md:1`。

## 玩法模块层

- **实现：** `src/Chorus.ExampleGame/IRedesignGameModule.cs:1` 是模块契约；`src/Chorus.ExampleGame/ExampleGameModule.cs:9` 声明清单、状态、效果和结果；`src/Chorus.GameServer/RedesignModuleCatalog.cs:37` 按哈希索引随附包。
- **配置 schema：** `CHORUS_MODULE_PACKAGES` 与 `CHORUS_INPUT_TRANSLATION` 在 `src/Chorus.GameServer/Program.cs:146` 校验。
- **专属协议文件：** 模块身份由 `src/Chorus.Rpc/GameModuleIdentity.cs:14` 承载；模块输入/效果是 `src/Chorus.Rpc/Protos/m2.proto:115` 中的 protobuf 字段。
- **文案挂载：** 模块指南为 `docs/guide/game-module.md:1`；面向模块的启动拒绝文案在 `src/Chorus.GameServer/Program.cs:169`。

## 持久数据层

- **实现：** `src/Chorus.DataServer/Program.cs:38` 按聚合实例化独立存储；`src/Chorus.DataServer/RedesignRoomStore.cs:30` 是房间事务所有者，`src/Chorus.DataServer/RedesignFrameStore.cs:1` 拥有帧/快照/恢复材料。
- **配置 schema：** 数据库连接与容量来自 `src/Chorus.DataServer/Program.cs:12` 和 `src/Chorus.DataServer/Program.cs:47`。
- **专属协议文件：** 数据服务和主播身份 RPC 在 `src/Chorus.Rpc/Protos/m0.proto:40`、`src/Chorus.Rpc/Protos/m1.proto:452`；SQL schema 从 `src/Chorus.Data/Schema/0002_core_schema.sql:1` 开始。
- **文案挂载：** 存储拒绝/状态字符串在 `src/Chorus.DataServer/RedesignRoomStore.cs:14`；保留与审计叙事为 `docs/architecture/audit-retention.md:1`。

## 平台适配层

- **实现：** 抽象在 `src/Chorus.Adapters.Abstractions/IPlatformEventVerifier.cs:3`；Bilibili、抖音、快手和小游戏实现在 `src/Chorus.Adapters.Platforms/`；任务侧网关装配在 `src/Chorus.PlatformGateway/Program.cs:121`。
- **配置 schema：** `src/Chorus.PlatformGateway/PlatformModules/PlatformModuleAssembly.cs:20` 选择模块，`src/Chorus.PlatformGateway/PlatformModules/PlatformModuleAssembly.cs:137` 拒绝缺失凭证。
- **专属协议文件：** Bilibili OpenLive 常量隔离在 `src/Chorus.Adapters.Platforms/Bilibili/BilibiliOpenLiveProtocol.cs:12`；入站数据统一为 `src/Chorus.Protocol/Models/AudienceEvent.cs:10`。
- **文案挂载：** 平台接入文案为 `docs/reference/platform-bilibili.md:1`、`docs/reference/platform-douyin.md:1`、`docs/reference/platform-kuaishou.md:1`，小游戏以 `docs/reference/platform-douyin-mini-game.md:1` 为代表。

## 集群监管层

- **实现：** `src/Chorus.NodeAgent/BootstrapAgent.cs:30` 监管子进程；`src/Chorus.ControlServer/ControlGrpcService.cs:15` 接收节点/进程上报；`src/Chorus.Rpc/ProcessSelfReporter.cs:17` 发布就绪与房间进度。
- **配置 schema：** 节点角色、租户、端口和自举证书在 `src/Chorus.BootstrapTool/Program.cs:48`；租约节奏在 `src/Chorus.ControlServer/OwnershipRenewalService.cs:16`。
- **专属协议文件：** `src/Chorus.Rpc/Protos/m0.proto:108` 定义 `ControlService`；进程证书辅助类型在 `src/Chorus.Rpc/ProcessIdentity.cs:1`。
- **文案挂载：** 部署文档为 `docs/reference/deployment.md:1`；服务安装描述为 `deploy/linux/chorus-node-agent.service:1`。

## 契约序列化层

- **实现：** protobuf 输入与生成客户端声明在 `src/Chorus.Rpc/Chorus.Rpc.csproj:13`；可移植 JSON 记录在 `src/Chorus.Protocol/Messages/` 和 `src/Chorus.Protocol/Models/`。
- **配置 schema：** 无；该层是生成契约加少量策略类型，如 `src/Chorus.Protocol/ProtocolVersionPolicy.cs:5`。
- **专属协议文件：** `src/Chorus.Rpc/Protos/m0.proto:1` 至 `src/Chorus.Rpc/Protos/m4.proto:1`。
- **文案挂载：** wire 名称嵌入 protobuf 消息；公开协议文档为 `docs/reference/protocol.md:1`。

## 客户端与工具层

- **实现：** `src/Chorus.Client/ChorusClient.cs:19` 是公开协议客户端；`src/Chorus.LoadTool/Program.cs:9` 驱动压测；`src/Chorus.ControlHost/Program.cs:64` 映射经认证的控制台 API；`src/Chorus.RoundDemo/RoundDemoScenario.cs:40` 是端到端演示。
- **配置 schema：** 控制台用户/提供方在 `src/Chorus.ControlHost/Program.cs:15`；压测参数模型在 `src/Chorus.LoadTool/LoadToolOptions.cs:9`。
- **专属协议文件：** 客户端消费 `src/Chorus.Rpc/` 的生成契约，以及 `src/Chorus.Protocol/` 的旧 JSON 消息。
- **文案挂载：** 控制台 UI 文案在 `src/Chorus.ControlHost/wwwroot/admin.js:1`；指南文案在 `docs/guide/load-testing.md:1`。

## 兼容运行层

- **实现：** 旧单体宿主为 `src/Chorus.Server/Program.cs:115`；旧引擎拆分为 `src/Chorus.EngineHost/Program.cs:42`；网关 IPC 与共享面为 `src/Chorus.Gateway.Runtime/GatewayHosting.cs:16` 和 `src/Chorus.Gateway.Core/Faces/StreamerWsFace.cs:18`。
- **配置 schema：** 旧 appsettings 为 `src/Chorus.Server/appsettings.json:1` 和 `src/Chorus.EngineHost/appsettings.json:1`。
- **专属协议文件：** IPC envelope 为 `src/Chorus.Messaging/Envelope.cs:1`；共享 WS 消息在 `src/Chorus.Protocol/Messages/`。
- **文案挂载：** 旧分层图在 `docs/architecture.md:26`，中文镜像为 `docs/zh/architecture.md:13`。

## 拆分理由

1. **边界与业务状态分离：** webhook 面明确没有数据库凭证，只返回持久数据面结果，避免监听策略变成房间状态所有权。证据：`src/Chorus.PlatformGateway/Program.cs:69`；`src/Chorus.PlatformGateway/PlatformIngressEndpoint.cs:24`。
2. **协调与存储分离：** 匹配票据和预留经 DataServer RPC 写入；ControlServer 只持有任期/恢复循环，不直接操作队列 SQL。证据：`src/Chorus.ControlServer/MatchCoordinatorService.cs:35`；`src/Chorus.DataServer/MatchGrpcService.cs:22`。
3. **运行时与玩法分离：** GameServer 选择哈希寻址的模块包并拒绝未知哈希，模块自身拥有 tick/检查点/离线语义。证据：`src/Chorus.GameServer/RedesignModuleCatalog.cs:105`；`src/Chorus.ExampleGame/ExampleGameModule.cs:9`。
4. **运行时与持久数据分离：** 房间提交和恢复加载都是远程 `FrameStore` 调用，GameServer 进程重启不等于业务恢复。证据：`src/Chorus.GameServer/RoomActor.cs:394`；`src/Chorus.GameServer/RoomRuntime.cs:1019`。
5. **入站适配与编排分离：** 适配器只验签/映射/去重；路由与持久接收仍是网关/数据面职责。证据：`src/Chorus.Adapters.Abstractions/IPlatformEventVerifier.cs:3`；`src/Chorus.PlatformGateway/PlatformIngressEndpoint.cs:31`。
6. **监管与业务恢复分离：** 进程上报不同于房间进度与所有权；进程存活不能激活恢复房间。证据：`src/Chorus.Rpc/ProcessSelfReporter.cs:17`；`src/Chorus.ControlServer/RedesignControlStore.cs:694`。
7. **契约隔离：** 每个跨进程调用和公开 WS 边缘都有显式协议产物，减少隐式内存假设。证据：`src/Chorus.Rpc/Chorus.Rpc.csproj:13`；`src/Chorus.StreamerGateway/Messages/EdgeTypes.cs:9`。
8. **客户端/工具隔离：** 压测与演示只驱动公开面，不导入生产存储。证据：`src/Chorus.LoadTool/Chorus.LoadTool.csproj:12`；`src/Chorus.RoundDemo/RoundDemoScenario.cs:40`。
9. **操作面隔离：** 控制台拥有认证、Origin 防护、静态 UI 和旧存储，而不是直接复用 Admin RPC。证据：`src/Chorus.ControlHost/Program.cs:15`；`src/Chorus.ControlHost/Program.cs:64`。
10. **兼容路径隔离：** 旧单体/拆分宿主可独立运行，重设计宿主不被反向并入它们。证据：`src/Chorus.Server/Program.cs:115`；`src/Chorus.PlatformGateway/Program.cs:121`。

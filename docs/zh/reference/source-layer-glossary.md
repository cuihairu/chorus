---
title: 源码分层术语表
---

# 源码分层术语表

本表记录源码中的执行职责，属于源码分层词汇，不声称每个项目只能属于一层。实现、配置、协议与文档锚点见[源码分层解说](../architecture/source-layers.md)。

| 术语 | 英文 | 定义 | file:line |
|---|---|---|---|
| 边界接入层 | Public Edge | 终止平台回调与主播 WebSocket，应用监听面策略，只转发已验签或已绑定会话的请求。 | src/Chorus.PlatformGateway/PlatformIngressEndpoint.cs:31 |
| 业务协调层 | Business Coordination | 持有跨进程的主播登录、挂载、匹配、恢复、暂停恢复和停止投递状态机。 | src/Chorus.ControlServer/MatchCoordinatorService.cs:34 |
| 对局运行层 | Game Runtime | 创建、激活、推进、同步、暂停、恢复和退役房间执行。 | src/Chorus.GameServer/RoomRuntime.cs:18 |
| 玩法模块层 | Gameplay Module | 声明模块节奏、检查点、离线策略、状态结构、确定性哈希和结果语义。 | src/Chorus.ExampleGame/ExampleGameModule.cs:9 |
| 持久数据层 | Durable Data | 通过 PostgreSQL 事务提交身份、房间、轮次、帧、结算、任务和排行事实。 | src/Chorus.DataServer/RedesignRoomStore.cs:30 |
| 平台适配层 | Platform Integration | 验证平台签名、映射载荷、去重事件并执行平台出站任务，不裁决房间权威。 | src/Chorus.Adapters.Platforms/Bilibili/BilibiliPlatformEventVerifier.cs:17 |
| 集群监管层 | Cluster Supervision | 校验进程身份、承载进程上报与所有权租约，并监管本机服务进程。 | src/Chorus.NodeAgent/BootstrapAgent.cs:30 |
| 契约序列化层 | Contract & Serialization | 集中保存生成的 gRPC 契约与生产消费双方共享的消息记录。 | src/Chorus.Rpc/Chorus.Rpc.csproj:13 |
| 客户端与工具层 | Client, Tooling & Operator | 把协议客户端、压测驱动、演示流程和操作控制台接入暴露面。 | src/Chorus.Client/ChorusClient.cs:19 |
| 兼容运行层 | Legacy Compatibility | 保留旧单体、控制台、引擎宿主、网关运行时和 IPC 装配，用于迁移对照。 | src/Chorus.Server/Program.cs:115 |
| 持久入口 | Durable Ingest | 平台回调先持久化事实，再返回调用方可见的回执。 | src/Chorus.PlatformGateway/PlatformIngressEndpoint.cs:24 |
| 匹配协调器 | Match Coordinator | 基于任期的后台循环，把票据推进到预留、拥有、准备、激活和提交。 | src/Chorus.ControlServer/MatchCoordinatorService.cs:28 |
| 房间运行时 | Room Runtime | 房间执行器、准入预算、结果、恢复与释放策略的进程内所有者。 | src/Chorus.GameServer/RoomRuntime.cs:18 |
| 房间执行器 | Room Actor | 单个串行房间状态机，产出 tick 结果并经数据面提交帧。 | src/Chorus.GameServer/RoomActor.cs:160 |
| 模块包 | Module Package | 宿主按哈希选择的清单加模块工厂；未知包哈希会拒绝启动。 | src/Chorus.GameServer/RedesignModuleCatalog.cs:15 |
| 轮次同步 | Round Sync | GameServer 的流式面，向网关发送快照、增量、回执、结果和挂起通知。 | src/Chorus.GameServer/RoundSyncGrpcService.cs:14 |
| 进程上报 | Process Report | 控制面心跳，携带端点、就绪、准入和逐房间进度。 | src/Chorus.Rpc/ProcessSelfReporter.cs:17 |
| 所有权租约 | Ownership Lease | 持久的 owner/generation/lease 记录，变更状态前隔离旧执行者。 | src/Chorus.ControlServer/RedesignControlStore.cs:390 |
| 迁移契约 | Migration Contract | 嵌入式编号 SQL 迁移，服务启动前校验 checksum。 | src/Chorus.Data/Schema/RedesignSchemaMigrator.cs:9 |
| 操作控制台 | Operational Console | 经认证的 HTTP 操作面，由旧控制存储和静态控制台资源支撑。 | src/Chorus.ControlHost/Program.cs:64 |

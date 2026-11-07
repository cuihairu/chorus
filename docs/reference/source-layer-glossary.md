---
title: Source Layer Glossary
---

# Source Layer Glossary

This table records the execution responsibilities found in source code. It is a source-layer vocabulary, not a promise that every project belongs to exactly one layer. Read [source layers](../architecture/source-layers.md) for implementation, configuration, protocol, and documentation anchors.

| 术语 | English | 定义 | file:line |
|---|---|---|---|
| 边界接入层 | Public Edge | Terminates platform callbacks and streamer WebSockets, applies listener policy, and forwards only verified or session-bound requests. | src/Chorus.PlatformGateway/PlatformIngressEndpoint.cs:31 |
| 业务协调层 | Business Coordination | Owns streamer login, mount, match, recovery, pause/resume, and stop-delivery state machines across cluster processes. | src/Chorus.ControlServer/MatchCoordinatorService.cs:34 |
| 对局运行层 | Game Runtime | Creates, activates, ticks, synchronizes, pauses, recovers, and retires room executions. | src/Chorus.GameServer/RoomRuntime.cs:18 |
| 玩法模块层 | Gameplay Module | Declares module cadence, checkpoint policy, offline policy, state schema, deterministic hash, and result semantics. | src/Chorus.ExampleGame/ExampleGameModule.cs:9 |
| 持久数据层 | Durable Data | Commits identity, room, round, frame, settlement, task, and leaderboard facts through PostgreSQL transactions. | src/Chorus.DataServer/RedesignRoomStore.cs:30 |
| 平台适配层 | Platform Integration | Verifies platform signatures, maps payloads, deduplicates events, and executes platform-bound tasks without deciding room authority. | src/Chorus.Adapters.Platforms/Bilibili/BilibiliPlatformEventVerifier.cs:17 |
| 集群监管层 | Cluster Supervision | Certifies process identity, carries process reports and ownership leases, and supervises local service processes. | src/Chorus.NodeAgent/BootstrapAgent.cs:30 |
| 契约序列化层 | Contract & Serialization | Centralizes generated gRPC faces and portable message records shared by producers and consumers. | src/Chorus.Rpc/Chorus.Rpc.csproj:13 |
| 客户端与工具层 | Client, Tooling & Operator | Connects clients, load drivers, demo scenarios, and the operator console to exposed faces. | src/Chorus.Client/ChorusClient.cs:19 |
| 兼容运行层 | Legacy Compatibility | Keeps the old monolith, console, engine host, gateway runtime, and IPC assembly available for comparison and migration. | src/Chorus.Server/Program.cs:115 |
| 持久入口 | Durable Ingest | The platform callback path that persists a fact before returning a caller-visible receipt. | src/Chorus.PlatformGateway/PlatformIngressEndpoint.cs:24 |
| 匹配协调器 | Match Coordinator | A term-based background loop that walks tickets through reserve, ownership, preparation, activation, and commit. | src/Chorus.ControlServer/MatchCoordinatorService.cs:28 |
| 房间运行时 | Room Runtime | The in-process map and policy owner for room actors, admission budgets, results, recovery, and release. | src/Chorus.GameServer/RoomRuntime.cs:18 |
| 房间执行器 | Room Actor | One serial room execution state machine that produces tick outcomes and commits frames through the data plane. | src/Chorus.GameServer/RoomActor.cs:160 |
| 模块包 | Module Package | A hash-addressed manifest plus module factory selected by the host; unknown package hashes refuse startup. | src/Chorus.GameServer/RedesignModuleCatalog.cs:15 |
| 轮次同步 | Round Sync | The GameServer streaming face that sends snapshots, deltas, receipts, results, and suspension notices to gateways. | src/Chorus.GameServer/RoundSyncGrpcService.cs:14 |
| 进程上报 | Process Report | The control-plane heartbeat carrying endpoint, readiness, admission, and per-room progress. | src/Chorus.Rpc/ProcessSelfReporter.cs:17 |
| 所有权租约 | Ownership Lease | A durable owner/generation/lease record that fences stale executors before state mutation. | src/Chorus.ControlServer/RedesignControlStore.cs:390 |
| 迁移契约 | Migration Contract | Embedded numbered SQL migrations verified by checksum before redesign services start. | src/Chorus.Data/Schema/RedesignSchemaMigrator.cs:9 |
| 操作控制台 | Operational Console | The authenticated HTTP operator surface backed by the legacy control store and static console assets. | src/Chorus.ControlHost/Program.cs:64 |

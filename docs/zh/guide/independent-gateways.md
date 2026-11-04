---
title: Independent Gateway Hosts
---

# 独立主播网关与平台网关

| 服务 | 项目 / 发布产物 | 对外入口 | 专属职责 |
|---|---|---|---|
| 平台网关 `platform-gw` | `Chorus.PlatformGateway` | `POST /ingress/{platform}` | 平台验签、事件映射、限流、去重、Bilibili Open Live 桥 |
| 主播网关 `streamer-gw` | `Chorus.StreamerGateway` | WebSocket `/ws` | 主播鉴权、连接、快照、ACK、重同步 |

两者是独立可执行程序，可以单独发布和扩缩容。入口由项目固定，`Gateway:Roles` 不会改变入口。
独立宿主的必填配置走 `CHORUS_*` 环境变量，实例身份来自进程证书；`Gateway:Id` 只标识经 IPC 挂到 EngineHost 下的网关（`GatewayIpcService.cs:71`），不标识这两个宿主的副本——副本用不同进程证书区分。

公共代码分层：

- `Chorus.Gateway.Runtime`：IPC、控制面路由与连接协调、共享诊断端点。没有应用入口，也不注册平台/主播专属服务。
- `Chorus.Gateway.Core`：入口协议实现及接口——跨宿主共享的监听面（由 `Chorus.Server` / `Chorus.EngineHost` 直接引用 `Chorus.Gateway.Core`）。
- `Chorus.Control` / `Chorus.Messaging` / `Chorus.Protocol`：控制面客户端、通信及消息契约。

旧单体 `Chorus.Gateway` 宿主已于 2026-10-31 移除（`48623a5`）；上面两个宿主就是仅有的网关可执行文件。
当前 Core 库仍包含两个 face，平台适配器程序集仍可传递到发布目录；隔离的是宿主、服务注册、端点与配置职责，并非完全剥离所有传递程序集。

## 本机启动

分别在三个终端运行。两个网关宿主都要求证书环境面（`Program.cs:9-15`），Kestrel 监听从 `CHORUS_*` 端口构建，`--urls` 不生效：

```bash
Ipc__GatewayIds=platform-gw,streamer-gw dotnet run --project src/Chorus.EngineHost

CHORUS_CLUSTER_ID=local CHORUS_ROOT_CA_PEM=./certs/root-ca.pem CHORUS_NODE_CA_PEM=./certs/node-ca.pem \
CHORUS_TLS_CERT_PEM=./certs/gw.pem CHORUS_TLS_KEY_PEM=./certs/gw.key CHORUS_DEV_PLATFORM_ENDPOINT=http://localhost:9000 \
CHORUS_PORT=7504 CHORUS_INGRESS_PORT=5168 dotnet run --project src/Chorus.PlatformGateway

CHORUS_CLUSTER_ID=local CHORUS_ROOT_CA_PEM=./certs/root-ca.pem CHORUS_NODE_CA_PEM=./certs/node-ca.pem \
CHORUS_TLS_CERT_PEM=./certs/gw.pem CHORUS_TLS_KEY_PEM=./certs/gw.key CHORUS_TENANT_APPLICATIONS=tenant-a:app-1 \
CHORUS_PORT=7530 CHORUS_WSS_PORT=5169 dotnet run --project src/Chorus.StreamerGateway
```

平台地址为 `https://localhost:5168/ingress/{platform}`（TLS 用服务器证书；仅设置 `CHORUS_INGRESS_PORT` 时才有该监听，`Program.cs:45-53`）；主播地址为 `wss://localhost:5169/ws`。`PlatformGateway/Program.cs:66` 要求 `CHORUS_DEV_PLATFORM_ENDPOINT`；`StreamerGateway/Program.cs:53` 要求 `CHORUS_TENANT_APPLICATIONS`。
平台密钥配置在平台宿主；`Faces:StreamerWs:AuthTokens` 配置在主播宿主。
共享诊断端点（`/health`、`/ready`、`/metrics`、`/faces`）实现在 `Chorus.Gateway.Runtime`（`GatewayHosting.cs`）；独立宿主拆分后它们仍只由 `Chorus.Server` / `Chorus.EngineHost` 映射，两个网关宿主暂未暴露。未所属的业务入口返回 404。

## 多机与独立发布

```bash
dotnet publish src/Chorus.PlatformGateway -c Release -o publish/platform-gateway
dotnet publish src/Chorus.StreamerGateway -c Release -o publish/streamer-gateway
```

多机时使用 TCP/HTTP IPC，分别配置可达的 listen/advertised endpoint 和唯一实例 ID。
所有 Gateway 和 EngineHost 使用同一个 `Control:Url` / `Control:Key`。
具体端点与租约参数见[部署说明](../reference/deployment.md)及[多机验收](../architecture/room-multihost-acceptance.md)。

控制模式下，一个 Worker 故障只使所属房间不可路由，不会因该房间拉低所有 Gateway 的 readiness。
路由快照过期或无健康 Worker 时仍返回不就绪。

`Control:AutomaticRecovery` 在 ControlHost 默认为 false。开启后维护循环会原子接管租约失效或实例已替换的房间，
增加 epoch 并进入 Recovering；健康 owner 不能被抢占。未通过独立主机网络分区/同步复制验收前保持关闭。
逐 tick 持久化契约保持不变，独立网关发布不代表已通过生产容量门槛。

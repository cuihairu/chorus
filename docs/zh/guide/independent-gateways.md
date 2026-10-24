---
title: Independent Gateway Hosts
---

# 独立主播网关与平台网关

| 服务 | 项目 / 发布产物 | 对外入口 | 专属职责 |
|---|---|---|---|
| 平台网关 `platform-gw` | `Chorus.PlatformGateway` | `POST /ingress/{platform}` | 平台验签、事件映射、限流、去重、Bilibili Open Live 桥 |
| 主播网关 `streamer-gw` | `Chorus.StreamerGateway` | WebSocket `/ws` | 主播鉴权、连接、快照、ACK、重同步 |

两者是独立可执行程序，可以单独发布和扩缩容。入口由项目固定，`Gateway:Roles` 不会改变入口。
`Gateway:Id` 是实例标识，同一服务的多个副本必须使用不同 ID，例如 `streamer-gw-01`。

公共代码分层：

- `Chorus.Gateway.Runtime`：IPC、控制面路由与连接协调、共享诊断端点。没有应用入口，也不注册平台/主播专属服务。
- `Chorus.Gateway.Core`：入口协议实现及接口，与单体宿主共享。
- `Chorus.Control` / `Chorus.Messaging` / `Chorus.Protocol`：控制面客户端、通信及消息契约。

旧 `Chorus.Gateway` 保留用于兼容原有部署，其可配置角色不作为新独立宿主的依赖。
当前 Core 库仍包含两个 face，平台适配器程序集仍可传递到发布目录；隔离的是宿主、服务注册、端点与配置职责，并非完全剥离所有传递程序集。

## 本机启动

分别在三个终端运行：

```bash
Ipc__GatewayIds=platform-gw,streamer-gw dotnet run --project src/Chorus.EngineHost
Gateway__Id=platform-gw dotnet run --project src/Chorus.PlatformGateway -- --urls http://localhost:5168
Gateway__Id=streamer-gw dotnet run --project src/Chorus.StreamerGateway -- --urls http://localhost:5169
```

平台地址为 `http://localhost:5168/ingress/{platform}`；主播地址为 `ws://localhost:5169/ws`。
平台密钥配置在平台宿主；`Faces:StreamerWs:AuthTokens` 配置在主播宿主。
开发测试才使用 `Ingress:AllowAll=true` 或 `Faces:StreamerWs:AllowAnonymous=true`。
两端均提供 `/health`、`/ready`、`/metrics`、`/faces`，未所属的业务入口返回 404。

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

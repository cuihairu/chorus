# Chorus.ControlServer

**Redesigned control plane** — gRPC control and coordination services
(`ControlGrpcService`, `AdminGrpcService`, `MatchCoordinatorService`), the
node/process registry (`RedesignControlStore`), lease renewal, and the `ops.*`
Postgres schema. Distinct from `Chorus.ControlHost`, which is the operations console
(minimal-API + static UI over the legacy `Chorus.Control` store) — the console does
not reuse the Admin RPC face (ops-surface isolation).

**重设计控制面** — gRPC 控制与协调服务（`ControlGrpcService`、`AdminGrpcService`、`MatchCoordinatorService`）、节点/进程注册表（`RedesignControlStore`）、租约续期与 `ops.*` Postgres schema。与 `Chorus.ControlHost`（旧 `Chorus.Control` 存储上的运营控制台，minimal-API + 静态 UI）互不复用 Admin RPC 面（操作面隔离）。

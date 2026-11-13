# Chorus.ControlHost

**Operations console host** — authenticated minimal-API plus static UI
(`wwwroot/admin.js`) over the legacy `Chorus.Control` store. Despite the shared
"Control" name, this is the human ops surface, not the control plane: it maps the
console REST API and keeps its own persistence. The redesigned control plane is
`Chorus.ControlServer` (gRPC `Control`/`MatchCoordinator` services, node/process
registry, `ops.*` Postgres schema).

**运营控制台宿主** — 认证 minimal-API + 静态 UI（`wwwroot/admin.js`），落在旧 `Chorus.Control` 存储上。虽然同样带 "Control" 命名，它是给人用的操作面而非控制面：映射控制台 REST API，持有独立持久化。重设计控制面是 `Chorus.ControlServer`（gRPC `Control`/`MatchCoordinator` 服务、节点/进程注册表、`ops.*` Postgres schema）。

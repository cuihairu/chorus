# Chorus.Control

**Legacy console persistence.** `ControlDatabase` is the transactional authority store
(SQLite for single-host, PostgreSQL for multi-worker) backing the operations-console
path `Chorus.ControlHost`. This is *not* the redesigned control plane — that is
`Chorus.ControlServer` (gRPC coordination services over the `ops.*` Postgres schema).
The console deliberately keeps its own store: ops-surface isolation means the console
owns auth, Origin protection, static UI, and the old storage, and does not reuse the
Admin RPC face.

**旧控制台持久化。** `ControlDatabase` 是事务权威存储（SQLite 单机、PostgreSQL 多 worker），支撑运营控制台路径 `Chorus.ControlHost`。它不是重设计控制面——那是 `Chorus.ControlServer`（`ops.*` Postgres schema 上的 gRPC 协调服务）。控制台刻意保有独立存储：操作面隔离要求控制台自带认证、Origin 防护、静态 UI 与旧存储，不复用 Admin RPC。

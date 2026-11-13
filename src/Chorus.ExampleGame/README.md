# Chorus.ExampleGame

**Redesign game modules.** `IRedesignGameModule` is the module contract: each module
declares a manifest (architecture 8.1) — tick cadence, checkpoint policy, and offline
policy are module facts, fixed by the module, not by the host. The shipped packages
(`ExampleGameModule` V1–V3, `DuelGameModule`, `LiveGameModule`) are consumed by
`Chorus.GameServer` via the hash-indexed `RedesignModuleCatalog`. This is the active
sample set for the redesigned path; the old-engine sample set is `Chorus.SampleGame`.

**重设计玩法模块。** `IRedesignGameModule` 是模块契约：每个模块自带清单（架构 8.1）——tick 节奏、检查点策略、离线策略都是模块事实，由模块钉死而非宿主。随附包（`ExampleGameModule` V1–V3、`DuelGameModule`、`LiveGameModule`）由 `Chorus.GameServer` 的哈希索引 `RedesignModuleCatalog` 消费。本项目是重设计路径的活跃示例集；旧引擎示例集为 `Chorus.SampleGame`。

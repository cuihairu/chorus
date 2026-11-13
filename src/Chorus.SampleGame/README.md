# Chorus.SampleGame

**Legacy-engine sample modules.** `SampleGameModule` implements the old `IGameModule`
contract (`Chorus.Engine.Abstractions`) and is consumed by the legacy hosts
`Chorus.Server` and `Chorus.EngineHost` only. This project is a frozen compatibility
surface kept for old-path tests and migration comparison — new game modules belong in
`Chorus.ExampleGame` (the redesign contract, `IRedesignGameModule`).

**旧引擎示例模块。** `SampleGameModule` 实现旧 `IGameModule` 契约（`Chorus.Engine.Abstractions`），仅被旧宿主 `Chorus.Server` 与 `Chorus.EngineHost` 消费。本项目是冻结的兼容面，仅供旧路径测试与迁移对照；新玩法模块一律进 `Chorus.ExampleGame`（重设计契约 `IRedesignGameModule`）。

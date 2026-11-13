# Chorus.RoundDemo

**End-to-end demo / acceptance driver.** `RoundDemoScenario` runs the full round
journey against a cluster (the M2 exit demonstration, including the guest-edge
disconnect variant) and emits evidence. It drives only the public RPC surface — like
`Chorus.LoadTool`, it is a client/tools-layer artifact living under `src/` on purpose,
not a production service and not a test fixture. Consumed by `Chorus.Engine.Tests`
acceptance tests (`RoundDemoScenarioTests`, `RedesignObs17OnlineChainAcceptanceTests`,
`RedesignRank09ExampleTests`).

**端到端演示/验收驱动。** `RoundDemoScenario` 对集群跑完整轮次旅程（M2 出箱演示，含 guest edge 掉线变体）并产出证据。它只驱动公开 RPC 面——与 `Chorus.LoadTool` 同类，属于客户端与工具层，刻意放在 `src/` 下：不是生产服务，也不是测试夹具。由 `Chorus.Engine.Tests` 验收测试消费（`RoundDemoScenarioTests`、`RedesignObs17OnlineChainAcceptanceTests`、`RedesignRank09ExampleTests`）。

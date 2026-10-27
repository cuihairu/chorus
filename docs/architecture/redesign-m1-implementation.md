---
title: M1 主播生命周期实施状态
---

# M1 主播生命周期实施状态

M1 按 [验收计划](redesign-acceptance.md) 实施；真实平台无法联调时允许用明确标记为模拟器的契约实现验证其余流程，但不能把模拟器标成生产适配器。完整目标见[架构第 5–6 章](architecture.md#5-主播登录实体激活和挂载)与[协议契约](redesign-contracts.md)。

## 本轮已落地：身份与登录凭据持久化基础

`RedesignIdentityStore` 在 PostgreSQL 上实现：

- Platform/AppId/OpenId 到稳定 StreamerId 的作用域映射，禁止同一平台身份被另一个 Tenant 重新认领。
- 新 Login 以 streamer 行锁串行化，SessionVersion 单调递增并撤销旧控制会话；只保存至少 256 bit resume secret 的 SHA-256 摘要。
- Resume 原子轮换 secret 与 SessionVersion；精确 OperationId/请求重试在两分钟窗口返回同一 SessionGrant；不同请求重用 operation、旧 secret 重放或已被新 Login 接管时拒绝。
- 会话数据库记录与映射使用现有 M0 `auth.*` schema，使用数据库事务和 streamer_state 行锁，不在事务中执行外部网络调用。

数据库集成证据：`RedesignIdentityStoreTests.PlatformIdentity_LoginTakeoverAndResumeRotationAreTenantScopedAndIdempotent`。此项是 M1 的持久化基础，尚不等于 StreamerServer API 或完整主播登录流程。

## 本轮已落地：平台启动验证接口与开发模拟器

`RedesignPlatformGateway` 按平台名路由 `VerifyLaunch`；`DevelopmentPlatformSimulator` 用 HMAC-SHA256 签发并验证开发启动凭据（AppId 作用域、过期、固定时间签名比较、稳定拒绝码、无签名秘密返回）；`DouyinLaunchAdapter`/`KuaishouLaunchAdapter` 为 `NotProductionReady` 失败关闭桩。生产宿主默认拒绝非 ProductionReady 适配器，开发宿主显式打开；官方协议版本、脱敏真实样本、签名正反用例与成功回执齐备前，桩不验证任何凭据，模拟器证据不折算为生产适配器。契约证据：`RedesignPlatformGatewayTests` 5/5（含伪造/过期/错 AppId/未知平台/越权适配器输出/凭据不外泄）。此项是 M1 平台接入前置，尚不等同 AT02（需真实平台样本）。

## M1 后续顺序

1. ~~PlatformGateway 的验证接口和 Development Platform Simulator；Douyin/Kuaishou 实现分别保留 `NotProductionReady`~~（已落地，见上）。
2. StreamerServer Login/Resume API、实体资料加载成功后才返回 LoginReady、access credential 校验和同连接 Attach generation。
3. StreamerActor / LoginCoordinator 状态恢复及 GameplayBinding 唯一挂载、跨 GameId LiveSlot 冲突事务。
4. 持久 PlatformTask、NormalizedEvent/UnresolvedInbox、回调提交后确认、乱序/迟到事件归属。
5. 逐项完成 AT01–AT09；外部真实平台需要测试应用/样本时，模拟器证据与生产验收分开记录。

目前没有 StreamerServer 宿主、完整轮换响应丢失集成测试、Actor 恢复、挂载流程或平台持久收件箱；平台启动验证接口与开发模拟器已落地，但真实平台适配器保持 `NotProductionReady`。M1 保持进行中，不将旧 Gateway/Actor 代码折算为新架构验收。

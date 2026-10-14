namespace Chorus.Adapters.Platforms;

/// <summary>
/// 抖音小游戏服务端 OpenAPI 接入参数（官方口径 2026-10-14 核实，见
/// docs/reference/platform-douyin.md）。AppId/AppSecret 是平台凭据：
/// 走配置注入，不进仓不进日志（官方安全要求：secret 泄露平台可能下架小游戏）。
/// ApiBaseUrl 仅供测试桩替换，生产恒用默认域名。
/// </summary>
public sealed record DouyinMiniGameOptions(
    string AppId,
    string AppSecret,
    string? ApiBaseUrl = null)
{
    public const string DefaultApiBaseUrl = "https://minigame.zijieapi.com";

    public string BaseUrl =>
        string.IsNullOrWhiteSpace(ApiBaseUrl) ? DefaultApiBaseUrl : ApiBaseUrl.TrimEnd('/');
}

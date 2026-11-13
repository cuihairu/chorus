using Chorus.Adapters.Abstractions;
using Microsoft.Extensions.Logging;

namespace Chorus.Adapters.Platforms;

/// <summary>
/// Douyu event-callback verifier SKELETON: <b>斗鱼官方技术 contract 一直未能从公开入口取得</b>
/// （弹幕互动创作者入口只返回站点通用内容；开放平台无版本化文档/SDK —— 见
/// docs/research/platforms/douyu-huya-bilibili-2026-10-30.md），官方签名/鉴权语义未知，
/// 因此<b>没有任何可实现的校验逻辑</b>。照 NotProductionReadyLaunchAdapter 的准入纪律，
/// 在生产准入证据齐备前 fail closed：拒绝一切回调，不验证任何凭据。
/// 生产准入证据清单：官方协议版本、脱敏真实样本、签名测试向量、成功回执格式。
/// 骨架预留 ≠ 支持；README/平台矩阵按 skeleton 级登记。
/// </summary>
public sealed class DouyuPlatformEventVerifier : IPlatformNamedEventVerifier
{
    private readonly ILogger<DouyuPlatformEventVerifier>? _logger;
    private int _skeletonNoticeLogged;

    public string PlatformName => PlatformNames.Douyu;

    public DouyuPlatformEventVerifier(ILogger<DouyuPlatformEventVerifier>? logger = null) => _logger = logger;

    public bool Verify(string platform, string rawBody, IReadOnlyDictionary<string, string> headers)
    {
        if (!string.Equals(platform, PlatformNames.Douyu, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (Interlocked.Exchange(ref _skeletonNoticeLogged, 1) == 0)
        {
            _logger?.LogWarning(
                "douyu event verifier is a skeleton: the official protocol contract is unavailable, " +
                "every callback is rejected (fail closed). It becomes implementable only after the " +
                "official signature semantics and sanitized real samples exist.");
        }

        return false;
    }
}

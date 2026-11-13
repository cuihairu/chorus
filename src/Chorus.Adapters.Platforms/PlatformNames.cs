namespace Chorus.Adapters.Platforms;

public static class PlatformNames
{
    public const string Test = "test";
    public const string Simulator = "simulator";
    public const string Douyin = "douyin";
    public const string Kuaishou = "kuaishou";
    public const string Bilibili = "bilibili";
    // 骨架占位：douyu 骨架预留（官方协议物料未取得），huya 仅研究（旧开放 API 2026-10-31 停服，
    // 替代路线未见）。常量本身不注册任何实现——支持状态以 README/平台矩阵登记为准。
    public const string Douyu = "douyu";
    public const string Huya = "huya";

    /// <summary>
    /// The one ingress-side normalization rule (review 2026-11-13 m1): a platform name
    /// entering through any host's ingress boundary is trimmed and lowercased into the
    /// canonical identifier before verification, mapping, dedup, storage, routing, and
    /// metrics read it — so "/ingress/DouYin" and "/ingress/douyin" are one platform
    /// namespace, not two split dedup/attribution lanes. Null for blank input; the faces
    /// answer that with their ordinary refusals (no verifier, unmapped).
    /// </summary>
    public static string? Normalize(string? platform)
        => string.IsNullOrWhiteSpace(platform) ? null : platform.Trim().ToLowerInvariant();
}

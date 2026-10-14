namespace Chorus.Adapters.Platforms.MiniGame;

/// <summary>
/// A single mini-game platform capability point that an adapter may rely on.
/// </summary>
public enum MiniGameCapability
{
    /// <summary>Client-side login code acquisition (e.g. tt.login).</summary>
    LoginClientCode = 0,

    /// <summary>Server-side login code → session exchange (code2session HTTP API).</summary>
    ServerSessionExchange = 1,

    /// <summary>WebSocket-style realtime channel available inside the mini-game container.</summary>
    RealtimeWebSocket = 2,

    /// <summary>Open data domain (sandbox context that may read relationship-chain data).</summary>
    OpenDataDomain = 3,

    /// <summary>Friend ranking / cloud storage read-write for leaderboards.</summary>
    FriendRankData = 4,
}

/// <summary>
/// Research status of a capability - the adapter never assumes a capability
/// that was not found in public official documentation (todo §17: 禁止编造
/// 平台能力).
/// </summary>
public enum MiniGameCapabilityStatus
{
    /// <summary>Confirmed by public official documentation (evidence URL recorded).</summary>
    VerifiedOfficial = 0,

    /// <summary>
    /// 未找到公开资料(待核)：public official documentation was not retrievable
    /// for this capability. Must not be treated as available.
    /// </summary>
    NotFoundPublicDocs = 1,
}

/// <summary>
/// One documented capability point with its research evidence.
/// <param name="OfficialApi">Official API name(s); null when no public doc exists.</param>
/// <param name="Evidence">Official doc URL, or the 待核 note explaining what was not found.</param>
/// </summary>
public sealed record MiniGameCapabilityEntry(
    MiniGameCapability Capability,
    MiniGameCapabilityStatus Status,
    string? OfficialApi,
    string Evidence);

/// <summary>
/// Public-docs-backed capability table for a mini-game platform (todo §17).
/// This table is the single source of truth that keeps adapters from
/// fabricating platform capabilities: <see cref="Supports"/> returns true only
/// for capabilities confirmed in public official documentation, and
/// <see cref="TryGet"/> returns null for an unknown platform instead of
/// guessing.
///
/// 凭据与真机联调项不进能力表——它们不是能力，是交付前置条件，单独记录在
/// <see cref="ExternalPending"/>（标「待外部」）。
/// </summary>
public sealed record MiniGamePlatformCapabilities(
    string Platform,
    IReadOnlyList<MiniGameCapabilityEntry> Entries,
    IReadOnlyList<string> ExternalPending)
{
    public MiniGameCapabilityEntry? Entry(MiniGameCapability capability)
        => Entries.FirstOrDefault(entry => entry.Capability == capability);

    /// <summary>Whether the capability is confirmed by public official documentation.</summary>
    public bool Supports(MiniGameCapability capability)
        => Entry(capability) is { Status: MiniGameCapabilityStatus.VerifiedOfficial };

    public MiniGamePlatformCapabilities? TryGet(string? platform)
    {
        return platform is null || string.IsNullOrWhiteSpace(platform)
            ? null
            : platform.Trim().ToLowerInvariant() switch
            {
                "douyin" => Douyin,
                "kuaishou" => Kuaishou,
                _ => null,
            };
    }

    /// <summary>
    /// 抖音小游戏（developer.open-douyin.com 官方公开文档，2026-10-14 调研）。
    /// API 名称取自官方 JavaScript API 索引页（静态可抓取），但该索引下的
    /// 各 API 正文页为 JS 渲染、公开抓取拿不到正文——因此只有「API 存在」被
    /// 核实，参数/字段/返回结构一律 未找到公开资料(待核)。
    /// </summary>
    public static MiniGamePlatformCapabilities Douyin => new(
        "douyin",
        new List<MiniGameCapabilityEntry>
        {
            new(
                MiniGameCapability.LoginClientCode,
                MiniGameCapabilityStatus.VerifiedOfficial,
                "tt.login / tt.checkSession",
                "https://developer.open-douyin.com/docs/resource/zh-CN/mini-game/develop/api/javascript-api/overview " +
                "(官方索引页列有 tt.login: /develop/api/open-capacity/log-in/tt-login；正文页 JS 渲染，返回字段未找到公开资料(待核))"),
            new(
                MiniGameCapability.ServerSessionExchange,
                MiniGameCapabilityStatus.NotFoundPublicDocs,
                null,
                "未找到公开资料(待核)：服务端 code2session 端点/参数/响应字段未获得公开正文" +
                "（服务端 OpenAPI 文档页为 JS 渲染，公开抓取仅返回导航壳）"),
            new(
                MiniGameCapability.RealtimeWebSocket,
                MiniGameCapabilityStatus.NotFoundPublicDocs,
                null,
                "未找到公开资料(待核)：官方小游戏 JavaScript API 索引的「网络」分类仅列 " +
                "tt.getNetworkType / tt.onNetworkStatusChange / tt.offNetworkStatusChange，" +
                "未见 socket / WebSocket API；容器内实时通道能力无法从公开资料确认"),
            new(
                MiniGameCapability.OpenDataDomain,
                MiniGameCapabilityStatus.VerifiedOfficial,
                "tt.getOpenDataContext / tt.onMessage",
                "https://developer.open-douyin.com/docs/resource/zh-CN/mini-game/develop/api/javascript-api/overview " +
                "(官方索引页列有开放数据域开放能力；正文页 JS 渲染，沙箱消息协议细节未找到公开资料(待核))"),
            new(
                MiniGameCapability.FriendRankData,
                MiniGameCapabilityStatus.VerifiedOfficial,
                "tt.setUserCloudStorage / tt.getUserCloudStorage / tt.getCloudStorageByRelation / " +
                "tt.removeUserCloudStorage / tt.getSharedCanvas；游戏排行 tt.setImRankData / tt.getImRankList / " +
                "tt.getImRankData / tt.setImRankDataInOpenContext",
                "https://developer.open-douyin.com/docs/resource/zh-CN/mini-game/develop/api/javascript-api/overview " +
                "(官方索引页列有开放数据/云存储与游戏排行 API；各 API 的数据结构与沙箱边界未找到公开资料(待核))"),
        },
        new List<string>
        {
            "小游戏 AppID / AppSecret（凭据，待外部提供，绝不写进代码）",
            "服务端 code2session 端点与参数（待核后接线，见 docs/reference/platform-douyin-mini-game.md）",
            "真机与开发者工具联调验证容器内实时通道（Socket）能力是否存在",
        });

    /// <summary>
    /// 快手小游戏（open.kuaishou.com 官方公开文档，2026-10-14 调研）。
    /// 公开文档站仅有「小程序」板块，未见「小游戏」板块——三项能力全部
    /// 未找到公开资料(待核)，不做任何平台能力假设。
    /// </summary>
    public static MiniGamePlatformCapabilities Kuaishou => new(
        "kuaishou",
        new List<MiniGameCapabilityEntry>
        {
            new(
                MiniGameCapability.LoginClientCode,
                MiniGameCapabilityStatus.NotFoundPublicDocs,
                null,
                "未找到公开资料(待核)：open.kuaishou.com 公开文档（/docs/develop/guide/introduction 等）" +
                "标题为「快手小程序」，未见小游戏板块与 ks.login 类 API"),
            new(
                MiniGameCapability.ServerSessionExchange,
                MiniGameCapabilityStatus.NotFoundPublicDocs,
                null,
                "未找到公开资料(待核)：仅核实到「小程序」code2Session" +
                "（https://open.kuaishou.com/docs/develop/server/code2Session.html，" +
                "POST https://open.kuaishou.com/oauth2/mp/code2session，js_code/app_id/app_secret → " +
                "result/session_key/open_id），该产品是小程序而非小游戏；小游戏是否复用未找到公开资料"),
            new(
                MiniGameCapability.RealtimeWebSocket,
                MiniGameCapabilityStatus.NotFoundPublicDocs,
                null,
                "未找到公开资料(待核)：公开文档未见小游戏 WebSocket / connectSocket 能力"),
            new(
                MiniGameCapability.OpenDataDomain,
                MiniGameCapabilityStatus.NotFoundPublicDocs,
                null,
                "未找到公开资料(待核)：公开文档未见小游戏开放数据域"),
            new(
                MiniGameCapability.FriendRankData,
                MiniGameCapabilityStatus.NotFoundPublicDocs,
                null,
                "未找到公开资料(待核)：公开文档未见小游戏好友排行/云存储能力"),
        },
        new List<string>
        {
            "小游戏平台入口本身是否独立于小程序平台存在（需开发者后台/登录态确认）",
            "小游戏 AppID / AppSecret / 消息验证 Token 等凭据（待外部提供）",
            "登录、实时通信、排行榜三段的 SDK 能力口径（待平台官方文档公开后调研）",
            "真机/开发者工具联调验证",
        });
}

using Chorus.Adapters.Platforms.MiniGame;

namespace Chorus.Engine.Tests;

/// <summary>
/// 小游戏平台能力表金测（todo §17）：能力表是防编造平台能力的唯一事实源——
/// 只有官方公开文档核实过的能力才 Supports()，未找到公开资料(待核)一律
/// 不可用，未知平台 fail-visible 返回 null。
/// </summary>
public sealed class MiniGamePlatformCapabilitiesTests
{
    [Fact]
    public void Douyin_VerifiedCapabilities_AreSupported()
    {
        var capabilities = MiniGamePlatformCapabilities.Douyin;

        // 官方索引页核实存在（API 名已核实，字段细节待核）。
        Assert.True(capabilities.Supports(MiniGameCapability.LoginClientCode));
        Assert.True(capabilities.Supports(MiniGameCapability.OpenDataDomain));
        Assert.True(capabilities.Supports(MiniGameCapability.FriendRankData));
    }

    [Fact]
    public void Douyin_UnverifiedCapabilities_AreNotSupported()
    {
        var capabilities = MiniGamePlatformCapabilities.Douyin;

        // 未找到公开资料(待核)：不得当作可用能力。
        Assert.False(capabilities.Supports(MiniGameCapability.ServerSessionExchange));
        Assert.False(capabilities.Supports(MiniGameCapability.RealtimeWebSocket));
    }

    [Fact]
    public void Douyin_UnverifiedEntries_CarryNotFoundEvidence()
    {
        var entry = MiniGamePlatformCapabilities.Douyin.Entry(MiniGameCapability.RealtimeWebSocket)!;

        Assert.Equal(MiniGameCapabilityStatus.NotFoundPublicDocs, entry.Status);
        Assert.Null(entry.OfficialApi);
        Assert.Contains("未找到公开资料", entry.Evidence);
    }

    [Fact]
    public void Douyin_VerifiedEntries_CarryOfficialApiAndDocUrl()
    {
        var entry = MiniGamePlatformCapabilities.Douyin.Entry(MiniGameCapability.LoginClientCode)!;

        Assert.Equal(MiniGameCapabilityStatus.VerifiedOfficial, entry.Status);
        Assert.Contains("tt.login", entry.OfficialApi);
        Assert.StartsWith("https://developer.open-douyin.com/", entry.Evidence);
    }

    [Fact]
    public void Kuaishou_NoMiniGamePublicDocs_NothingSupported()
    {
        var capabilities = MiniGamePlatformCapabilities.Kuaishou;

        // 快手公开文档仅覆盖小程序：小游戏全部能力未找到公开资料(待核)。
        Assert.False(capabilities.Supports(MiniGameCapability.LoginClientCode));
        Assert.False(capabilities.Supports(MiniGameCapability.ServerSessionExchange));
        Assert.False(capabilities.Supports(MiniGameCapability.RealtimeWebSocket));
        Assert.False(capabilities.Supports(MiniGameCapability.OpenDataDomain));
        Assert.False(capabilities.Supports(MiniGameCapability.FriendRankData));
    }

    [Fact]
    public void Kuaishou_Entries_ExplainWhatWasNotFound()
    {
        var entry = MiniGamePlatformCapabilities.Kuaishou.Entry(MiniGameCapability.ServerSessionExchange)!;

        Assert.Equal(MiniGameCapabilityStatus.NotFoundPublicDocs, entry.Status);
        Assert.Null(entry.OfficialApi);
        Assert.Contains("未找到公开资料", entry.Evidence);
    }

    [Fact]
    public void ExternalPending_CredentialsAndJointDebugging_MarkedPerPlatform()
    {
        // 凭据/真机联调是交付前置条件，不是能力——单独标「待外部」。
        Assert.NotEmpty(MiniGamePlatformCapabilities.Douyin.ExternalPending);
        Assert.NotEmpty(MiniGamePlatformCapabilities.Kuaishou.ExternalPending);
        Assert.Contains(MiniGamePlatformCapabilities.Douyin.ExternalPending, item => item.Contains("待外部"));
        Assert.Contains(MiniGamePlatformCapabilities.Kuaishou.ExternalPending, item => item.Contains("待外部"));
    }

    [Fact]
    public void TryGet_UnknownPlatform_ReturnsNull()
    {
        Assert.Null(MiniGamePlatformCapabilities.Douyin.TryGet("ghost"));
        Assert.Null(MiniGamePlatformCapabilities.Douyin.TryGet(null));
        Assert.Null(MiniGamePlatformCapabilities.Douyin.TryGet(" "));
    }

    [Fact]
    public void TryGet_KnownPlatforms_ReturnCaseInsensitive()
    {
        Assert.NotNull(MiniGamePlatformCapabilities.Douyin.TryGet("douyin"));
        Assert.NotNull(MiniGamePlatformCapabilities.Douyin.TryGet("Douyin"));
        Assert.NotNull(MiniGamePlatformCapabilities.Douyin.TryGet("kuaishou"));
        Assert.NotNull(MiniGamePlatformCapabilities.Douyin.TryGet("KUAISHOU"));
    }

    [Fact]
    public void Entries_ContainEveryKnownCapabilityExactlyOnce()
    {
        var capabilities = MiniGamePlatformCapabilities.Douyin;

        // 能力表覆盖全部已知能力点，不重复登记（缺项会漏判，重复项语义冲突）。
        Assert.Equal(Enum.GetValues<MiniGameCapability>().Length, capabilities.Entries.Count);
        Assert.Equal(capabilities.Entries.Count, capabilities.Entries.Select(e => e.Capability).Distinct().Count());
    }

    [Fact]
    public void Entry_DefaultCapability_IsRegistered()
    {
        // LoginClientCode 是枚举 0；默认值也是已登记能力，行为一致。
        Assert.Equal(
            MiniGameCapability.LoginClientCode,
            MiniGamePlatformCapabilities.Douyin.Entry(default).Capability);
    }
}
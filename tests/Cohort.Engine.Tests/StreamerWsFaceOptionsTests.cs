using Cohort.Gateway.Core.Faces;
using Microsoft.Extensions.Configuration;

namespace Cohort.Engine.Tests;

/// <summary>
/// Unit coverage for streamer-ws face auth: token parsing (plain and
/// expiry-suffixed), expiry, anonymous mode, and fail-closed with no config.
/// </summary>
public sealed class StreamerWsFaceOptionsTests
{
    private const long NowMs = 1_700_000_000_000;

    private static StreamerWsFaceOptions Options(Dictionary<string, string?> settings)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        return StreamerWsFaceOptions.FromConfiguration(configuration);
    }

    [Fact]
    public void Parse_PlainAndExpiryTokens()
    {
        var options = Options(new Dictionary<string, string?>
        {
            ["Faces:StreamerWs:AuthTokens"] = "tok-plain; tok-exp@1700000060000 ;broken@;leading@x",
        });

        Assert.Equal(4, options.Tokens.Count);
        Assert.Equal("tok-plain", options.Tokens[0].Value);
        Assert.Null(options.Tokens[0].ExpiresAtMs);
        Assert.Equal("tok-exp", options.Tokens[1].Value);
        Assert.Equal(1_700_000_060_000, options.Tokens[1].ExpiresAtMs);
        Assert.Equal("broken@", options.Tokens[2].Value);
        Assert.Equal("leading@x", options.Tokens[3].Value);
    }

    [Fact]
    public void Parse_EmptyOrMissing_YieldsNoTokens()
    {
        Assert.Empty(Options(new Dictionary<string, string?> { ["Faces:StreamerWs:AuthTokens"] = "" }).Tokens);
        Assert.Empty(Options(new Dictionary<string, string?>()).Tokens);
        Assert.Empty(Options(new Dictionary<string, string?> { ["Faces:StreamerWs:AuthTokens"] = "  ;  " }).Tokens);
    }

    [Fact]
    public void Defaults_AreFailClosed()
    {
        var options = Options(new Dictionary<string, string?>());

        Assert.False(options.AllowAnonymous);
        Assert.Equal(64 * 1024, options.MaxMessageBytes);
        Assert.Equal(600, options.AckPerMinute);

        Assert.False(options.Authenticate("anything", NowMs).Ok);
        Assert.Equal("missing_token", options.Authenticate(null, NowMs).Reason);
    }

    [Fact]
    public void Authenticate_ValidToken_Passes()
    {
        var options = Options(new Dictionary<string, string?> { ["Faces:StreamerWs:AuthTokens"] = "tok-good" });

        Assert.True(options.Authenticate("tok-good", NowMs).Ok);
    }

    [Fact]
    public void Authenticate_ExpiredToken_Fails_WithReason()
    {
        var options = Options(new Dictionary<string, string?> { ["Faces:StreamerWs:AuthTokens"] = $"tok@{NowMs - 1}" });

        var result = options.Authenticate("tok", NowMs);

        Assert.False(result.Ok);
        Assert.Equal("token_expired", result.Reason);
    }

    [Fact]
    public void Authenticate_TokenAtExpiryBoundary_IsExpired()
    {
        var options = Options(new Dictionary<string, string?> { ["Faces:StreamerWs:AuthTokens"] = $"tok@{NowMs}" });

        Assert.False(options.Authenticate("tok", NowMs).Ok);
        Assert.True(options.Authenticate("tok", NowMs - 1).Ok);
    }

    [Fact]
    public void Authenticate_UnknownToken_Fails_WithReason()
    {
        var options = Options(new Dictionary<string, string?> { ["Faces:StreamerWs:AuthTokens"] = "tok-good" });

        var result = options.Authenticate("tok-other", NowMs);

        Assert.False(result.Ok);
        Assert.Equal("invalid_token", result.Reason);
    }

    [Fact]
    public void Authenticate_MissingToken_AnonymousMode_Passes()
    {
        var options = Options(new Dictionary<string, string?> { ["Faces:StreamerWs:AllowAnonymous"] = "true" });

        Assert.True(options.Authenticate(null, NowMs).Ok);
        Assert.True(options.Authenticate("  ", NowMs).Ok);
    }

    [Fact]
    public void Authenticate_TokenMatch_BeatsAnonymous()
    {
        var options = Options(new Dictionary<string, string?>
        {
            ["Faces:StreamerWs:AuthTokens"] = "tok-good",
            ["Faces:StreamerWs:AllowAnonymous"] = "true",
        });

        Assert.True(options.Authenticate("tok-good", NowMs).Ok);
        Assert.False(options.Authenticate("tok-other", NowMs).Ok);
    }
}

using System.Text.Json;
using Chorus.LoadTool;

namespace Chorus.Engine.Tests;

/// <summary>
/// Load-tool unit coverage: the Douyin payload shape the mapper accepts, the
/// duplicate-id behaviour, and CLI option parsing.
/// </summary>
public sealed class LoadToolTests
{
    [Fact]
    public void Payload_TestPlatformHasTestMapperFields()
    {
        var factory = new EventPayloadFactory("test", userPoolSize: 100, giftRate: 0, duplicateRate: 0, matchId: "m2");
        var payload = factory.Create("load-s1");

        using var doc = JsonDocument.Parse(payload);
        var root = doc.RootElement;

        Assert.Equal("load-s1", root.GetProperty("sessionId").GetString());
        Assert.True(root.TryGetProperty("eventId", out _));
        Assert.True(root.TryGetProperty("userId", out _));
        Assert.True(root.GetProperty("kind").GetString() is "comment" or "like");
        Assert.Equal("m2", root.GetProperty("matchId").GetString());
    }

    [Fact]
    public void Payload_HasDouyinMapperFields()
    {
        var factory = new EventPayloadFactory("douyin", userPoolSize: 100, giftRate: 0, duplicateRate: 0, matchId: null);
        var payload = factory.Create("load-s1");

        using var doc = JsonDocument.Parse(payload);
        var root = doc.RootElement;

        Assert.Equal("load-s1", root.GetProperty("session_id").GetString());
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("event_id").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("user_id").GetString()));
        Assert.True(root.GetProperty("event").GetString() is "comment" or "like");
    }

    [Fact]
    public void Payload_GiftEventsCarryGiftFields()
    {
        var factory = new EventPayloadFactory("douyin", userPoolSize: 100, giftRate: 1, duplicateRate: 0, matchId: "m1");
        var payload = factory.Create("load-s1");

        using var doc = JsonDocument.Parse(payload);
        var root = doc.RootElement;

        Assert.Equal("gift", root.GetProperty("event").GetString());
        Assert.True(root.TryGetProperty("gift_id", out _));
        Assert.True(root.TryGetProperty("gift_count", out _));
        Assert.Equal("m1", root.GetProperty("match_id").GetString());
    }

    [Fact]
    public void DuplicateRate_ReusesRecentEventIds()
    {
        var factory = new EventPayloadFactory("douyin", userPoolSize: 100, giftRate: 0, duplicateRate: 1, matchId: null);
        var first = JsonDocument.Parse(factory.Create("s")).RootElement.GetProperty("event_id").GetString();

        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < 20; i++)
        {
            using var doc = JsonDocument.Parse(factory.Create("s"));
            seen.Add(doc.RootElement.GetProperty("event_id").GetString()!);
        }

        Assert.Contains(first!, seen);
        Assert.True(seen.Count < 20, "with dup-rate 1.0 every id should come from the recent window");
    }

    [Fact]
    public void Options_ParsesFlags()
    {
        var options = LoadToolOptions.Parse(new[]
        {
            "--url", "http://127.0.0.1:5168",
            "--sessions", "7",
            "--rate", "10",
            "--clients", "2",
            "--duration", "30",
            "--ack-mix", "slow",
            "--match", "m9",
        });

        Assert.Equal("http://127.0.0.1:5168", options.BaseUrl);
        Assert.Equal(7, options.Sessions);
        Assert.Equal(10, options.EventsPerSecondPerSession);
        Assert.Equal(2, options.ClientsPerSession);
        Assert.Equal(30, options.DurationSeconds);
        Assert.Equal("slow", options.AckMix);
        Assert.Equal("m9", options.MatchId);
    }

    [Fact]
    public void Options_EnvironmentOverridesDefaults()
    {
        var previous = Environment.GetEnvironmentVariable("CHORUS_LOAD_URL");
        try
        {
            Environment.SetEnvironmentVariable("CHORUS_LOAD_URL", "http://10.0.0.2:5168");
            var options = LoadToolOptions.Parse(Array.Empty<string>());
            Assert.Equal("http://10.0.0.2:5168", options.BaseUrl);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CHORUS_LOAD_URL", previous);
        }
    }
}

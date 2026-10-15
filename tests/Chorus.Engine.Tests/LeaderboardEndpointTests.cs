using System.Net;
using System.Text.Json;
using Chorus.Data;
using Chorus.Protocol;
using Chorus.Protocol.Models;
using Chorus.SampleGame;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Chorus.Engine.Tests;

/// <summary>
/// Leaderboard encapsulation (docs/guide/leaderboard.md): the room board is
/// written only by the session's game module through ISessionScoreSink
/// (replace-projection, audit §9.2), and read through the read-only
/// /leaderboard/{scope}/{scopeId} endpoint on the two session-owning hosts.
/// </summary>
public sealed class LeaderboardEndpointTests
{
    public sealed class ServerFactory : WebApplicationFactory<Chorus.Server.Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("Ingress:AllowAll", "true");
        }
    }

    public sealed class EngineHostFactory : WebApplicationFactory<global::Chorus.EngineHost.Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            // Random socket dir so concurrent EngineHost hosts never collide on
            // the default /tmp/chorus.
            builder.UseSetting("Ipc:UnixSocketDir", $"/tmp/chorus-test-lb-{Guid.NewGuid():N}");
        }
    }

    [Fact]
    public void SampleModule_ProjectsTouchedParticipants_IntoRoomBoard()
    {
        var store = new LeaderboardStore();
        var factory = new SampleGameModuleFactory(new Chorus.Gateway.Core.Leaderboard.LeaderboardScoreSink(store));
        using var game = factory.Create("s-lb");

        game.ApplyEvents(1, new AudienceEvent[]
        {
            new(EventId: "e1", Platform: "test", SessionId: "s-lb", UserId: "u1",
                Kind: AudienceEventKind.Like, IngestTimeMs: 1, Text: "2"),
            new(EventId: "e2", Platform: "test", SessionId: "s-lb", UserId: "u1",
                Kind: AudienceEventKind.Gift, IngestTimeMs: 2, GiftCount: 3),
            new(EventId: "e3", Platform: "test", SessionId: "s-lb", UserId: "u2",
                Kind: AudienceEventKind.Comment, IngestTimeMs: 3),
            // Enter/RoomLifecycle never score (and never appear on the board).
            new(EventId: "e4", Platform: "test", SessionId: "s-lb", UserId: "u3",
                Kind: AudienceEventKind.Enter, IngestTimeMs: 4),
        });

        var entries = store.Query(LeaderboardScope.Room, "s-lb");
        Assert.Equal(2, entries.Count);
        // Sample rule: likes + gifts + comments (2 + 3 for u1, 1 for u2).
        Assert.Equal("u1", entries[0].ParticipantId);
        Assert.Equal(5, entries[0].Score);
        Assert.Equal(1, entries[0].Rank);
        Assert.Equal("u2", entries[1].ParticipantId);
        Assert.Equal(1, entries[1].Score);
        Assert.Equal(2, entries[1].Rank);
    }

    [Fact]
    public void SampleModule_FrozenBoard_GameKeepsRunning_StandingsFrozen()
    {
        var store = new LeaderboardStore();
        var factory = new SampleGameModuleFactory(new Chorus.Gateway.Core.Leaderboard.LeaderboardScoreSink(store));
        using var game = factory.Create("s-frozen");

        game.ApplyEvents(1, new[] { Like("e1", "u1") });
        store.Freeze(LeaderboardScope.Room, "s-frozen");

        // More events after the freeze: the module must keep playing (a frozen
        // board is a settlement fact, not a game error) and the standings must
        // stay at the freeze point.
        game.ApplyEvents(2, new[] { Like("e2", "u1"), Like("e3", "u2") });

        var entries = store.Query(LeaderboardScope.Room, "s-frozen");
        Assert.Single(entries);
        Assert.Equal("u1", entries[0].ParticipantId);
        Assert.Equal(1, entries[0].Score);
    }

    [Fact]
    public void RestoreState_ReprojectsRoomBoard()
    {
        // Host restart = fresh in-memory store; restoring the session must
        // rebuild the room board from the authoritative snapshot.
        var store = new LeaderboardStore();
        var factory = new SampleGameModuleFactory(new Chorus.Gateway.Core.Leaderboard.LeaderboardScoreSink(store));
        using var game = factory.Create("s-restore");

        var state = JsonSerializer.SerializeToElement(new
        {
            tickId = 7L,
            matchId = (string?)null,
            users = new
            {
                likes = new Dictionary<string, long> { ["u1"] = 5 },
                gifts = new Dictionary<string, long> { ["u1"] = 2 },
                comments = new Dictionary<string, long> { ["u2"] = 3 },
            },
        }, ProtocolJson.SerializerOptions);
        game.RestoreState(1, state);

        var entries = store.Query(LeaderboardScope.Room, "s-restore");
        Assert.Equal(2, entries.Count);
        Assert.Equal(("u1", 7L, 1), (entries[0].ParticipantId, entries[0].Score, entries[0].Rank));
        Assert.Equal(("u2", 3L, 2), (entries[1].ParticipantId, entries[1].Score, entries[1].Rank));
    }

    [Fact]
    public void ServerEndpoint_UnknownScope_Returns400()
    {
        using var factory = new ServerFactory();
        var response = factory.CreateClient().GetAsync("/leaderboard/week/s-1").GetAwaiter().GetResult();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        Assert.Contains("unknown_scope", body);
    }

    [Fact]
    public void ServerEndpoint_TopOutOfRange_Returns400()
    {
        using var factory = new ServerFactory();
        var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.BadRequest, client.GetAsync("/leaderboard/room/s-1?top=0").GetAwaiter().GetResult().StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            client.GetAsync($"/leaderboard/room/s-1?top={Chorus.Gateway.Core.Leaderboard.LeaderboardHttp.MaxTop + 1}").GetAwaiter().GetResult().StatusCode);
    }

    [Fact]
    public void ServerEndpoint_UnknownBoard_ReturnsEmptyEntries()
    {
        using var factory = new ServerFactory();

        var json = JsonDocument.Parse(factory.CreateClient()
            .GetStringAsync("/leaderboard/room/s-never").GetAwaiter().GetResult());

        Assert.Equal("room", json.RootElement.GetProperty("scope").GetString());
        Assert.Equal("s-never", json.RootElement.GetProperty("scopeId").GetString());
        Assert.False(json.RootElement.GetProperty("frozen").GetBoolean());
        Assert.Empty(json.RootElement.GetProperty("entries").EnumerateArray());
    }

    [Fact]
    public void ServerEndpoint_SeededBoard_ReturnsCompetitionRanking()
    {
        using var factory = new ServerFactory();
        var store = factory.Services.GetRequiredService<LeaderboardStore>();
        store.UpsertScore(LeaderboardScope.Room, "s-rank", "u2", 50);
        store.UpsertScore(LeaderboardScope.Room, "s-rank", "u3", 50);
        store.UpsertScore(LeaderboardScope.Room, "s-rank", "u1", 30);

        var json = JsonDocument.Parse(factory.CreateClient()
            .GetStringAsync("/leaderboard/room/s-rank?top=2").GetAwaiter().GetResult());

        var entries = json.RootElement.GetProperty("entries").EnumerateArray().ToArray();
        Assert.Equal(2, entries.Length);
        // Ties share the competition rank; equal scores order by participant id.
        Assert.Equal(("u2", 50L, 1), (entries[0].GetProperty("participantId").GetString(),
            entries[0].GetProperty("score").GetInt64(), entries[0].GetProperty("rank").GetInt32()));
        Assert.Equal(("u3", 50L, 1), (entries[1].GetProperty("participantId").GetString(),
            entries[1].GetProperty("score").GetInt64(), entries[1].GetProperty("rank").GetInt32()));
    }

    [Fact]
    public void EngineHostEndpoint_SharesTheProjectedStore()
    {
        using var factory = new EngineHostFactory();
        // Seed through the same singleton the module factory writes to: the
        // endpoint must serve the projected board, not a private copy.
        var store = factory.Services.GetRequiredService<LeaderboardStore>();
        store.UpsertScore(LeaderboardScope.Room, "s-eh", "u1", 10);

        var json = JsonDocument.Parse(factory.CreateClient()
            .GetStringAsync("/leaderboard/room/s-eh").GetAwaiter().GetResult());

        var entries = json.RootElement.GetProperty("entries").EnumerateArray().ToArray();
        Assert.Single(entries);
        Assert.Equal("u1", entries[0].GetProperty("participantId").GetString());
        Assert.Equal(10, entries[0].GetProperty("score").GetInt64());
    }

    private static AudienceEvent Like(string eventId, string userId) =>
        new(EventId: eventId, Platform: "test", SessionId: "s-frozen", UserId: userId,
            Kind: AudienceEventKind.Like, IngestTimeMs: 1);
}

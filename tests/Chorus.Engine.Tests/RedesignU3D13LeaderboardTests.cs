using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Security;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Chorus.ControlServer;
using Chorus.Rpc;
using Chorus.StreamerGateway;
using Chorus.StreamerGateway.Messages;
using Chorus.UnitySdk;
using Chorus.UnitySdk.Edge;
using Npgsql;
using Xunit;

namespace Chorus.Engine.Tests;

/// <summary>
/// The framework leaderboard reads (FIX-U3D-13): the three RANK-04/05 queries ride the
/// edge's own WSS lane, the tenant and the game are stamped from the connection's session
/// facts (never client-supplied), and the read key — board/period/scope/subject — is the
/// whole request, because a wrong key reads an empty board rather than another board's
/// rows. Stale is an answer the game acts on (restart the walk), found=false is an answer
/// the game displays. The leaves pin the SDK↔transport mapping over a recording fake and
/// the pre-wire refusals over the real edge transport; the real link runs against a real
/// match cluster: a played round's committed score effects project onto the faction board
/// through the scoring module's own recipe, and a lobby client with no stream at all
/// walks the board — top, rank, around, pagination, the stale continuation and the
/// restart that sees the new world.
/// </summary>
public sealed class RedesignU3D13LeaderboardTests
{
    // ---- the leaves: the SDK↔transport mapping over a recording fake ----

    [Fact]
    public async Task ATopReadNamesItsKeyAndRidesTheAnswerBack()
    {
        var transport = new RecordingTransport
        {
            NextAnswer = new LeaderboardAnswer
            {
                Found = true,
                Entries =
                [
                    new LeaderboardRankedWireEntry { Rank = 1, SubjectId = "red", Score = 12 }
                ],
                ReadVersion = "v7",
                NextCursor = "next-1",
                HasMore = true,
            },
        };
        var session = await Session(transport);
        var key = new LeaderboardKey
        {
            BoardId = "faction-score", ScopeId = "room-1", Subject = LeaderboardSubject.Faction
        };

        var top = await session.GetTopAsync(key, 10, null, CancellationToken.None);

        // The intent named the key and only the key: the board, the default period, the
        // room's scope, the subject type — and nothing of tenant, game or credentials,
        // which are the session's facts the edge stamps on the other side.
        var intent = transport.LastQuery!;
        Assert.StartsWith("lb-", intent.OperationId);
        Assert.Equal("top", intent.Kind);
        Assert.Equal("faction-score", intent.BoardId);
        // The unset period rides null on the intent; the wire normalizes it to "" and the
        // edge synthesizes "alltime" on the read key.
        Assert.Null(intent.PeriodId);
        Assert.Equal("room-1", intent.ScopeId);
        Assert.Equal((int)LeaderboardSubject.Faction, intent.Subject);
        Assert.Equal(10, intent.MaxEntries);
        Assert.Null(intent.Cursor);
        // The answer maps back with nothing invented: the entries lose the wire's rank
        // (a top page's order is the board's order), the version and cursor survive.
        Assert.True(top.HasMore);
        Assert.Equal("v7", top.ReadVersion);
        Assert.Equal("next-1", top.NextCursor);
        var entry = Assert.Single(top.Entries);
        Assert.Equal("red", entry.SubjectId);
        Assert.Equal(12, entry.Score);

        // The next page rides the same lane with its cursor.
        await session.GetTopAsync(key, 10, "next-1", CancellationToken.None);
        Assert.Equal("next-1", transport.LastQuery!.Cursor);
        Assert.Equal(2, transport.QueryCount);
    }

    [Fact]
    public async Task APositionReadDefaultsItsSubjectAndAnswersFoundFalseOpenly()
    {
        var transport = new RecordingTransport
        {
            NextAnswer = new LeaderboardAnswer { Found = false },
        };
        var session = await Session(transport);
        var key = new LeaderboardKey { BoardId = "faction-score", ScopeId = "room-1" };

        // The rank names no subject: the connection's own identity is the subject (the
        // SDK passes null through; the edge defaults it from the session).
        var rank = await session.GetRankAsync(key, null, CancellationToken.None);
        Assert.Null(transport.LastQuery!.SubjectId);
        Assert.Equal(0, transport.LastQuery.Window);
        // Found=false is an answer, not an error: an absent subject reads as zeros.
        Assert.False(rank.Found);
        Assert.Equal(0, rank.Rank);
        Assert.Equal(string.Empty, rank.SubjectId);
        Assert.Equal(0, rank.Score);

        // The around names its subject and its window; the window rides the same intent.
        var around = await session.GetAroundMeAsync(key, "p2", 3, CancellationToken.None);
        Assert.Equal("around", transport.LastQuery!.Kind);
        Assert.Equal("p2", transport.LastQuery.SubjectId);
        Assert.Equal(3, transport.LastQuery.Window);
        Assert.False(around.Found);
        Assert.Empty(around.Entries);

        // An empty-string subject is refused by the session itself — null means "my own",
        // "" names nothing.
        await Assert.ThrowsAsync<ArgumentException>(() =>
            session.GetRankAsync(key, "", CancellationToken.None));
    }

    // ---- the wire boundary: the refusals the edge transport makes before any socket ----

    [Fact]
    public async Task AReadBeyondItsBoundsIsRefusedBeforeTheWire()
    {
        // The transport is constructed but never connected: every refusal below happens
        // in the validation walk, before the socket is even required.
        var transport = new EdgeChorusTransport(
            RedesignU3D07LoginLinkTests.ConfigurationLike(
                RedesignU3D07LoginLinkTests.FakeAdapterLike("simulator", "open-u3d13-bounds", "c"), 9),
            (_, _, _, _) => true);

        await Assert.ThrowsAsync<ArgumentException>(() => transport.LeaderboardQueryAsync(
            new LeaderboardQueryIntent
            {
                OperationId = "lb-1", Kind = "top", BoardId = "faction-score", MaxEntries = 0,
            }, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => transport.LeaderboardQueryAsync(
            new LeaderboardQueryIntent
            {
                OperationId = "lb-2", Kind = "top", BoardId = "faction-score", MaxEntries = 1001,
            }, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => transport.LeaderboardQueryAsync(
            new LeaderboardQueryIntent
            {
                OperationId = "lb-3", Kind = "around", BoardId = "faction-score", Window = 0,
            }, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => transport.LeaderboardQueryAsync(
            new LeaderboardQueryIntent
            {
                OperationId = "lb-4", Kind = "around", BoardId = "faction-score", Window = 51,
            }, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => transport.LeaderboardQueryAsync(
            new LeaderboardQueryIntent
            {
                OperationId = "lb-5", Kind = "top", BoardId = "faction-score", Subject = 7,
                MaxEntries = 10,
            }, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => transport.LeaderboardQueryAsync(
            new LeaderboardQueryIntent
            {
                OperationId = "lb-6", Kind = "top", BoardId = "", MaxEntries = 10,
            }, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => transport.LeaderboardQueryAsync(
            new LeaderboardQueryIntent
            {
                OperationId = "lb-7", Kind = "peek", BoardId = "faction-score",
            }, CancellationToken.None));
    }

    // ---- the real link: a played round, its projection, and the reads ----

    [Fact]
    public async Task APlayedRoundProjectsOntoTheBoardTheSdkReads()
    {
        await using var environment = await RedesignM0HostTests.HostEnvironment.CreateAsync();
        var (wssPort, simulator) = await RedesignM4HostTests.StartEdgeClusterAsync(environment,
            withMatchmaker: true);
        var certs = Trusts(environment.RootCertificatePem, environment.NodeAuthorityCertificatePem);

        // Two streamers play a round: both subscribe and pump (each send's receipt rides
        // its own stream), and each lands one boost — the module commits one score effect
        // per faction, the sender's own.
        var first = await LoginAndStartAsync(wssPort, simulator, "open-u3d13-a", certs);
        var second = await LoginAndStartAsync(wssPort, simulator, "open-u3d13-b", certs);
        await first.JoinMatchAsync("standard", null, CancellationToken.None);
        await second.JoinMatchAsync("standard", null, CancellationToken.None);
        var room = await first.AwaitRoomAsync(CancellationToken.None);
        await second.AwaitRoomAsync(CancellationToken.None);

        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        var firstStream = await first.SubscribeAsync(room.RoomId, room.RoomEpoch, 0, 0, budget.Token);
        var secondStream = await second.SubscribeAsync(room.RoomId, room.RoomEpoch, 0, 0, budget.Token);
        var firstPump = PumpAsync(firstStream, room.RoomId, room.RoomEpoch, budget.Token);
        var secondPump = PumpAsync(secondStream, room.RoomId, room.RoomEpoch, budget.Token);

        // Level 3 beats level 2 beyond the module's deterministic seed noise
        // (10*3 + [0..4] is always above 10*2 + [0..4]), so the first booster leads
        // the board no matter which faction either seat drew.
        await first.SendIntentAsync(new ClientIntent
        {
            RoomId = room.RoomId, RoomEpoch = room.RoomEpoch,
            CommandId = "cmd-u3d13-a1", PayloadJson = """{"action":"boost","level":3}""",
        }, budget.Token);
        await second.SendIntentAsync(new ClientIntent
        {
            RoomId = room.RoomId, RoomEpoch = room.RoomEpoch,
            CommandId = "cmd-u3d13-b1", PayloadJson = """{"action":"boost","level":2}""",
        }, budget.Token);

        await first.DisconnectAsync();
        await second.DisconnectAsync();
        await SwallowAsync(firstPump);
        await SwallowAsync(secondPump);

        // 存活性等待: the boosts' commits reach the ledger before the projection reads it.
        await WaitForScoreEffectsAsync(environment.DatabaseConnectionString, room.RoomId, 2, budget.Token);
        var effects = await ScoreEffectsAsync(environment.DatabaseConnectionString, room.RoomId, budget.Token);
        Assert.Equal(2, effects.Count);
        var read = effects.Select(effect =>
        {
            using var payload = JsonDocument.Parse(effect.Payload);
            var faction = payload.RootElement.GetProperty("faction").GetString()!;
            var delta = payload.RootElement.GetProperty("delta").GetInt64();
            return (effect.EffectId, Faction: faction, Delta: delta);
        }).ToList();
        var lead = read.Single(entry => entry.Delta >= 25);
        var trailer = read.Single(entry => entry.Delta < 25);
        Assert.Contains(lead.Faction, new[] { "red", "blue" });
        Assert.Contains(trailer.Faction, new[] { "red", "blue" });
        Assert.NotEqual(lead.Faction, trailer.Faction);

        // The projection is the scoring module's own recipe (RANK-09): name each
        // committed effect — the board (faction-score, scope = the room, all-time) is
        // the module's declared mapping, never the caller's.
        var controlCertificate = environment.Certificate("@cluster", "control-server", "u3d13-projector");
        using var projectionChannel = environment.Channel(environment.DataPort, controlCertificate,
            "data-server");
        var scoring = new LeaderboardScoringService(
            new GrpcLeaderboardScoringDataPlane(new Leaderboard.LeaderboardClient(projectionChannel)));
        foreach (var (effectId, _, _) in read)
            await scoring.ProjectScoreEffectAsync("tenant-a", "game-a", effectId, budget.Token);

        // A third session — a lobby client with no match and no stream — reads the
        // board: the query leg pumps its own short loop when no stream exists.
        var reader = await LoginAndStartAsync(wssPort, simulator, "open-u3d13-c", certs);
        var key = new LeaderboardKey
        {
            BoardId = "faction-score", ScopeId = room.RoomId, Subject = LeaderboardSubject.Faction
        };

        var top = await reader.GetTopAsync(key, 100, null, budget.Token);
        Assert.Equal(2, top.Entries.Count);
        Assert.Equal(lead.Faction, top.Entries[0].SubjectId);
        Assert.Equal(trailer.Faction, top.Entries[1].SubjectId);
        Assert.False(top.HasMore);
        Assert.Null(top.NextCursor);
        Assert.False(string.IsNullOrEmpty(top.ReadVersion));

        var leadRank = await reader.GetRankAsync(key, lead.Faction, budget.Token);
        Assert.True(leadRank.Found);
        Assert.Equal(1, leadRank.Rank);
        Assert.Equal(lead.Faction, leadRank.SubjectId);
        var trailerRank = await reader.GetRankAsync(key, trailer.Faction, budget.Token);
        Assert.True(trailerRank.Found);
        Assert.Equal(2, trailerRank.Rank);

        // The default subject and an unknown subject both read as absent: a faction
        // board never knows the streamer's own id, and 键=身份 — a wrong key is an empty
        // answer, never an error.
        var own = await reader.GetRankAsync(key, null, budget.Token);
        Assert.False(own.Found);
        var stranger = await reader.GetRankAsync(key, "spectator", budget.Token);
        Assert.False(stranger.Found);

        var window = await reader.GetAroundMeAsync(key, trailer.Faction, 5, budget.Token);
        Assert.True(window.Found);
        Assert.Equal(2, window.Entries.Count);
        Assert.Equal(1, window.Entries[0].Rank);
        Assert.Equal(lead.Faction, window.Entries[0].SubjectId);
        Assert.Equal(2, window.Entries[1].Rank);
        Assert.Equal(trailer.Faction, window.Entries[1].SubjectId);
        var ownWindow = await reader.GetAroundMeAsync(key, null, 5, budget.Token);
        Assert.False(ownWindow.Found);

        // The walk binds to its snapshot: page one mints the cursor and the version,
        // page two finishes the board on the same version.
        var pageOne = await reader.GetTopAsync(key, 1, null, budget.Token);
        var firstEntry = Assert.Single(pageOne.Entries);
        Assert.Equal(lead.Faction, firstEntry.SubjectId);
        Assert.True(pageOne.HasMore);
        Assert.NotNull(pageOne.NextCursor);
        var pageTwo = await reader.GetTopAsync(key, 1, pageOne.NextCursor, budget.Token);
        var secondEntry = Assert.Single(pageTwo.Entries);
        Assert.Equal(trailer.Faction, secondEntry.SubjectId);
        Assert.False(pageTwo.HasMore);
        Assert.Null(pageTwo.NextCursor);
        Assert.Equal(pageOne.ReadVersion, pageTwo.ReadVersion);

        // A committed effect lands mid-walk: the old cursor's snapshot is gone and the
        // continuation answers stale — the SDK turns the wire's stale word into the
        // exception a game restarts from.
        await InjectCommittedScoreEffectAsync(environment.DatabaseConnectionString, room.RoomId,
            trailer.Faction, trailerRank.Score + 100, budget.Token);
        var controlCertificate2 = environment.Certificate("@cluster", "control-server", "u3d13-staler");
        using var staleChannel = environment.Channel(environment.DataPort, controlCertificate2,
            "data-server");
        var staler = new LeaderboardScoringService(
            new GrpcLeaderboardScoringDataPlane(new Leaderboard.LeaderboardClient(staleChannel)));
        await staler.ProjectScoreEffectAsync("tenant-a", "game-a", $"{room.RoomId}/99000/0", budget.Token);
        await Assert.ThrowsAsync<LeaderboardStaleException>(() =>
            reader.GetTopAsync(key, 1, pageOne.NextCursor, budget.Token));

        // The restart from page one sees the new world: the boosted faction leads now.
        var restarted = await reader.GetTopAsync(key, 10, null, budget.Token);
        Assert.Equal(2, restarted.Entries.Count);
        Assert.Equal(trailer.Faction, restarted.Entries[0].SubjectId);
        Assert.Equal(lead.Faction, restarted.Entries[1].SubjectId);
        var demoted = await reader.GetRankAsync(key, lead.Faction, budget.Token);
        Assert.True(demoted.Found);
        Assert.Equal(2, demoted.Rank);

        // A board nobody wrote answers empty (键=身份), not an error.
        var unknown = await reader.GetTopAsync(
            new LeaderboardKey { BoardId = "no-such-board", ScopeId = room.RoomId,
                Subject = LeaderboardSubject.Faction },
            10, null, budget.Token);
        Assert.Empty(unknown.Entries);
        Assert.False(unknown.HasMore);

        await reader.DisconnectAsync();
    }

    [Fact]
    public async Task AQueryWithoutGameplayIsRefusedVisiblyOnTheWire()
    {
        await using var environment = await RedesignM0HostTests.HostEnvironment.CreateAsync();
        var (wssPort, simulator) = await RedesignM4HostTests.StartEdgeClusterAsync(environment);
        var rootPem = environment.RootCertificatePem;
        var nodePem = environment.NodeAuthorityCertificatePem;

        // The SDK's own flow always starts gameplay at connect, so the read gate's two
        // refusals are the raw wire's to prove. First, no session at all: the session
        // gate answers before any lane does.
        using var wire = await RedesignM4WsClient.ConnectAsync(wssPort, rootPem, nodePem);
        await wire.HelloAsync();
        await wire.SendAsync(new EdgeLeaderboardQuery
        {
            OperationId = "lb-u3d13-raw", Kind = "top", Board = "faction-score",
            PeriodId = "", ScopeId = "", Subject = 2, MaxEntries = 10, Window = 0,
        });
        var sessionless = await wire.ExpectAsync(EdgeTypes.Error, TimeSpan.FromSeconds(30));
        Assert.Equal(EdgeErrorCodes.NoSession, sessionless.GetProperty("code").GetString());

        // Then a login without gameplay: the read's own gate names the query's operation
        // id, and the connection stays open — the refused read was the query's answer.
        var secret = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        var credential = simulator.IssueCredential("app-1", "open-u3d13-raw", "live-open-u3d13-raw",
            TimeSpan.FromMinutes(5));
        await wire.SendAsync(new EdgeLoginWithPlatformLaunch
        {
            Platform = "simulator", AppId = "app-1", LaunchCredential = credential,
            LoginAttemptId = "attempt-u3d13-raw", NextResumeSecret = secret
        });
        await wire.ExpectAsync(EdgeTypes.LoginReady, TimeSpan.FromSeconds(30));
        await wire.SendAsync(new EdgeLeaderboardQuery
        {
            OperationId = "lb-u3d13-raw2", Kind = "top", Board = "faction-score",
            PeriodId = "", ScopeId = "", Subject = 2, MaxEntries = 10, Window = 0,
        });
        var refused = await wire.ExpectAsync(EdgeTypes.Error, TimeSpan.FromSeconds(30));
        Assert.Equal(EdgeErrorCodes.InvalidMessage, refused.GetProperty("code").GetString());
        Assert.Equal("lb-u3d13-raw2", refused.GetProperty("operationId").GetString());
        await AssertPingWorksAsync(wire);
    }

    // ---- helpers the facts share ----

    /// <summary>The game's pump loop: advance the stream and decide every frame — the
    /// reads and receipts of this fact ride it.</summary>
    private static async Task PumpAsync(ISyncStream stream, string roomId, long roomEpoch,
        CancellationToken cancellationToken)
    {
        var applier = new SyncApplier(roomId, roomEpoch, DeltaContractMode.Full);
        try
        {
            while (await stream.MoveNextAsync(cancellationToken))
            {
                applier.Decide(stream.Current);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
    }

    private static async Task SwallowAsync(Task pump)
    {
        try
        {
            await pump;
        }
        catch
        {
            // The disconnect ends the pump's read mid-wait; everything after the facts
            // held is teardown.
        }
    }

    /// <summary>存活性等待: poll the committed-effect ledger until the room's score effects
    /// have all landed — never a sleep guess.</summary>
    private static async Task WaitForScoreEffectsAsync(string connectionString, string roomId,
        int count, CancellationToken cancellationToken)
    {
        while (true)
        {
            var effects = await ScoreEffectsAsync(connectionString, roomId, cancellationToken);
            if (effects.Count >= count) return;
            await Task.Delay(200, cancellationToken);
        }
    }

    private static async Task<List<(string EffectId, string Payload)>> ScoreEffectsAsync(
        string connectionString, string roomId, CancellationToken cancellationToken)
    {
        var effects = new List<(string EffectId, string Payload)>();
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            SELECT effect_id, payload::text FROM infra.outbox
            WHERE kind = 'score' AND effect_id LIKE @room || '/%'
            ORDER BY committed_at, effect_id
            """, connection);
        command.Parameters.AddWithValue("room", roomId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            effects.Add((reader.GetString(0), reader.GetString(1)));
        return effects;
    }

    /// <summary>Plants one extra committed score effect in the ledger (the projection
    /// fixture's own recipe): a tick far beyond the round's length, so it collides with
    /// no real effect id.</summary>
    private static async Task InjectCommittedScoreEffectAsync(string connectionString, string roomId,
        string faction, long total, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO infra.outbox(effect_id, kind, payload, status, committed_at)
            VALUES (@effect, 'score', @payload::jsonb, 'Pending', @committed_at)
            ON CONFLICT (effect_id) DO NOTHING
            """, connection);
        command.Parameters.AddWithValue("effect", $"{roomId}/99000/0");
        command.Parameters.AddWithValue("payload",
            $$"""{"faction":"{{faction}}","delta":100,"total":{{total}}}""");
        command.Parameters.AddWithValue("committed_at", DateTimeOffset.UtcNow);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<ChorusSession> LoginAndStartAsync(int wssPort,
        Chorus.Platform.DevelopmentPlatformSimulator simulator, string openId,
        RemoteCertificateValidationCallback certs)
    {
        var credential = simulator.IssueCredential("app-1", openId, "live-" + openId, TimeSpan.FromMinutes(5));
        var adapter = RedesignU3D07LoginLinkTests.FakeAdapterLike("simulator", openId, credential);
        var sdk = new ChorusSdk(configuration => new EdgeChorusTransport(configuration, certs));
        var configuration = RedesignU3D07LoginLinkTests.ConfigurationLike(adapter, wssPort);
        return await sdk.ConnectAsync(configuration, adapter, CancellationToken.None);
    }

    private static async Task<ChorusSession> Session(RecordingTransport transport)
    {
        var adapter = RedesignU3D07LoginLinkTests.FakeAdapterLike("simulator", "open-1", """{"credential":"c"}""");
        var sdk = new ChorusSdk(_ => transport);
        return await sdk.ConnectAsync(RedesignU3D07LoginLinkTests.ConfigurationLike(adapter, 9), adapter,
            CancellationToken.None);
    }

    private static async Task AssertPingWorksAsync(RedesignM4WsClient wire)
    {
        // The ping uplink carries no fields beyond its type, so it goes out as raw text.
        await wire.SendRawAsync("""{"type":"ping"}""");
        await wire.ExpectAsync(EdgeTypes.Pong, TimeSpan.FromSeconds(30));
    }

    private static RemoteCertificateValidationCallback Trusts(string rootPem, string nodeAuthorityPem)
    {
        // The certs are NOT disposed: the closure must keep them alive for every
        // handshake this transport runs.
        var root = System.Security.Cryptography.X509Certificates.X509Certificate2.CreateFromPem(rootPem);
        var nodeAuthority = System.Security.Cryptography.X509Certificates.X509Certificate2.CreateFromPem(
            nodeAuthorityPem);
        return (_, certificate, _, _) =>
        {
            if (certificate is null) return false;
            using var candidate = new System.Security.Cryptography.X509Certificates.X509Certificate2(certificate);
            using var validator = new System.Security.Cryptography.X509Certificates.X509Chain();
            validator.ChainPolicy.TrustMode =
                System.Security.Cryptography.X509Certificates.X509ChainTrustMode.CustomRootTrust;
            validator.ChainPolicy.RevocationMode =
                System.Security.Cryptography.X509Certificates.X509RevocationMode.NoCheck;
            validator.ChainPolicy.CustomTrustStore.Add(root);
            validator.ChainPolicy.ExtraStore.Add(nodeAuthority);
            return validator.Build(candidate);
        };
    }

    /// <summary>The transport the mapping facts control: it remembers the last query and
    /// answers exactly what the fact set — every other leg is one these facts never walk.</summary>
    private sealed class RecordingTransport : IChorusTransport
    {
        public LeaderboardQueryIntent? LastQuery { get; private set; }
        public int QueryCount { get; private set; }
        public LeaderboardAnswer NextAnswer { get; set; } = new();

        public Task ConnectAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<LoginAnswer> LoginAsync(LoginIntent intent, CancellationToken cancellationToken) =>
            Task.FromResult(new LoginAnswer
            {
                StreamerId = "streamer-1",
                SessionId = "session-1",
                SessionVersion = 1,
                AccessCredential = "access-1",
                AccessExpiresAtUnixMs = 1,
                Duplicate = false,
                ResumeRegistered = true,
            });

        public Task<string> StartGameplayAsync(string accessCredential, string gameId, string operationId,
            CancellationToken cancellationToken) => Task.FromResult("binding-1");

        public Task<MatchTicket> JoinMatchAsync(JoinMatchIntent intent, CancellationToken cancellationToken) =>
            Task.FromResult(new MatchTicket { TicketId = "ticket-1", State = "Queued" });

        public Task<MatchTicket> CancelMatchAsync(string operationId, string ticketId,
            CancellationToken cancellationToken) =>
            Task.FromResult(new MatchTicket { TicketId = ticketId, State = "Cancelled" });

        public Task<JoinedRoom> AwaitRoomAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new JoinedRoom
            {
                RoomId = "room-1", RoomEpoch = 7, Participation = "home", ModuleVersion = "module-1",
                ConfigHash = "cfg-1", TicketId = "ticket-1", ReservationId = "res-1",
            });

        public Task<ISyncStream> SubscribeAsync(string roomId, long epoch, long lastAppliedTick,
            long lastAppliedStateVersion, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("the leaderboard facts never subscribe.");

        public Task<IntentReply> SendIntentAsync(ClientIntent intent, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("the leaderboard facts never send intents.");

        public Task<LoginAnswer> ResumeAsync(ResumeIntent intent, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("the leaderboard facts never resume.");

        public Task<JoinedRoom> AwaitRejoinedRoomAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("the leaderboard facts never await a re-entry.");

        public Task LeaveRoundAsync(string roomId, string operationId, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("the leaderboard facts never leave a round.");

        public Task LogoutAsync(string operationId, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("the leaderboard facts never log out.");

        public Task<LeaderboardAnswer> LeaderboardQueryAsync(LeaderboardQueryIntent intent,
            CancellationToken cancellationToken)
        {
            LastQuery = intent;
            QueryCount++;
            return Task.FromResult(NextAnswer);
        }

        public Task CloseAsync() => Task.CompletedTask;
    }
}

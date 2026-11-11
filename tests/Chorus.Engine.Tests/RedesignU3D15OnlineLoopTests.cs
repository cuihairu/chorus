using System;
using System.Collections.Generic;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using Chorus.ControlServer;
using Chorus.Platform;
using Chorus.Rpc;
using Chorus.UnitySdk;
using Chorus.UnitySdk.Edge;
using Chorus.UnitySdk.Samples;
using Npgsql;

namespace Chorus.Engine.Tests;

/// <summary>
/// The online sample (FIX-U3D-15): the shipped Samples~ file — the exact source a game
/// imports — runs one complete session against a real cluster with only its
/// configuration per seat: login, match, atomic entry, snapshot baseline, boosts, a
/// disconnect and the resume's catch-up mid-round, the round's frozen result, the
/// board's projection reads, the logout. Two seats run it concurrently into the same
/// room — the sample is the deliverable, the fact is its proof. The long round window
/// keeps the resume mid-round instead of racing the settlement.
/// </summary>
public sealed class RedesignU3D15OnlineLoopTests
{
    [Fact]
    public async Task TheSampleRunsTheWholeOnlineLoopWithOnlyItsConfiguration()
    {
        await using var environment = await RedesignM0HostTests.HostEnvironment.CreateAsync();
        var (wssPort, simulator) = await RedesignM4HostTests.StartEdgeClusterAsync(environment,
            withMatchmaker: true, roundDurationTicks: 800);
        var certs = Trusts(environment.RootCertificatePem, environment.NodeAuthorityCertificatePem);
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(300));

        // The game's operational side (RANK-09): the projection is the scoring module's
        // own recipe, run by the operator beside the cluster — the fact plays that duty
        // with a continuous projector, so the seats' reads meet a served board the way
        // production's projector loop serves one.
        using var projectorDone = new CancellationTokenSource();
        var projector = ProjectContinuouslyAsync(environment, projectorDone.Token);

        // Two seats, the sample once each, concurrently — the only per-seat input is the
        // configuration (endpoint, tenant, game, app) and the adapter's launch credential.
        var firstTask = RunSeatAsync(wssPort, simulator, "open-u3d15-a", certs, budget.Token);
        var secondTask = RunSeatAsync(wssPort, simulator, "open-u3d15-b", certs, budget.Token);
        await Task.WhenAll(firstTask, secondTask);
        var first = await firstTask;
        var second = await secondTask;
        projectorDone.Cancel();
        await projector;

        foreach (var report in new[] { first, second })
        {
            // Login and binding: the mount was exchanged for a real session.
            Assert.False(string.IsNullOrEmpty(report.SessionId));
            Assert.False(string.IsNullOrEmpty(report.BindingId));
            Assert.False(string.IsNullOrEmpty(report.TicketId));
            Assert.False(string.IsNullOrEmpty(report.Participation));
            // Baseline, moves, play: the applier baselined and the boosts were received
            // (a sequence each, no duplicate word).
            Assert.True(report.BaselineApplied);
            Assert.Equal(2, report.IntentSequences.Count);
            Assert.All(report.IntentSequences, sequence => Assert.True(sequence > 0));
            Assert.False(report.AnyIntentDuplicate);
            Assert.True(report.DeltasBeforeDisconnect >= 2);
            // The resume landed mid-round: same room, same epoch (no recovery bumped it),
            // the applier continued its own baseline forward.
            Assert.True(report.Rejoined);
            Assert.Equal(report.RoomId, report.RejoinedRoomId);
            Assert.Equal(report.RoomEpoch, report.RejoinedEpoch);
            Assert.False(report.RebaselineOnRejoin);
            Assert.True(report.CatchupFrames >= 1);
            Assert.True(report.VersionAfterCatchup >= report.VersionAtDisconnect);
            // The round froze, and the story carries the whole loop's shape.
            Assert.True(report.RoundEnded);
            Assert.False(string.IsNullOrEmpty(report.RoundResultJson));
            // The loop's last word (the story's legs name their moments in milliseconds).
            Assert.EndsWith("logged-out", report.Story[report.Story.Count - 1]);
        }
        Assert.Equal(first.RoomId, second.RoomId);
        // The board the round's settlement projected: the lead entry, its rank-one word,
        // the window around it, the cursor's second page, and the honest absent word for
        // a player subject on a faction board.
        Assert.True(first.TopEntries >= 1);
        Assert.True(first.LeadScore > 0);
        Assert.True(first.LeadRankFound);
        Assert.Equal(1, first.LeadRank);
        Assert.True(first.AroundEntries >= 1);
        Assert.False(first.OwnRankFound);
        Assert.Equal(first.TopEntries >= 2 ? 1 : 0, first.CursorPageEntries);
    }

    private static async Task<SampleReport> RunSeatAsync(int wssPort,
        DevelopmentPlatformSimulator simulator, string openId,
        RemoteCertificateValidationCallback certs, CancellationToken cancellationToken)
    {
        var credential = simulator.IssueCredential("app-1", openId, "live-" + openId,
            TimeSpan.FromMinutes(5));
        var adapter = RedesignU3D07LoginLinkTests.FakeAdapterLike("simulator", openId, credential);
        var sdk = new ChorusSdk(configuration => new EdgeChorusTransport(configuration, certs));
        var configuration = RedesignU3D07LoginLinkTests.ConfigurationLike(adapter, wssPort);
        try
        {
            return await SampleOnlineLoop.RunAsync(sdk, configuration, adapter,
                new SampleOptions { RoundEndBudget = TimeSpan.FromSeconds(120) }, cancellationToken);
        }
        catch (Exception error)
        {
            // The story rides the failure: the legs with their moments, and the edge's
            // own code when the refusal is one of its words.
            var code = (error as Chorus.UnitySdk.Edge.EdgeProtocolException)?.Code ?? "none";
            var story = error.Data["chorus-story"] as string ?? "none";
            throw new InvalidOperationException(
                "seat " + openId + " failed: code=" + code + " story: " + story, error);
        }
    }

    /// <summary>The operator's projector loop: walk the ledger's committed score
    /// effects and name each once to the scoring service (the module's own recipe,
    /// RANK-09) — the loop a real game runs beside the cluster.</summary>
    private static async Task ProjectContinuouslyAsync(
        RedesignM0HostTests.HostEnvironment environment, CancellationToken cancellationToken)
    {
        var controlCertificate = environment.Certificate("@cluster", "control-server", "u3d15-projector");
        using var projectionChannel = environment.Channel(environment.DataPort, controlCertificate,
            "data-server");
        var scoring = new LeaderboardScoringService(
            new GrpcLeaderboardScoringDataPlane(new Leaderboard.LeaderboardClient(projectionChannel)));
        var projected = new HashSet<string>();
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                foreach (var effectId in await CommittedScoreEffectsAsync(
                    environment.DatabaseConnectionString, cancellationToken))
                    if (projected.Add(effectId))
                        await scoring.ProjectScoreEffectAsync("tenant-a", "game-a", effectId,
                            cancellationToken);
                await Task.Delay(500, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // The fact's end is the loop's end.
        }
    }

    /// <summary>Every committed score effect in the ledger, oldest first — the
    /// projector's walk never names a room (the ledger is the walk's whole world).</summary>
    private static async Task<List<string>> CommittedScoreEffectsAsync(string connectionString,
        CancellationToken cancellationToken)
    {
        var effects = new List<string>();
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            SELECT effect_id FROM infra.outbox
            WHERE kind = 'score'
            ORDER BY committed_at, effect_id
            """, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            effects.Add(reader.GetString(0));
        return effects;
    }

    /// <summary>The test certificates chain to a private root: the transport's
    /// certificate override validates against that root and its node authority, the
    /// same shape the transport contract tests pin.</summary>
    private static RemoteCertificateValidationCallback Trusts(string rootPem, string nodeAuthorityPem)
    {
        // The certs are NOT disposed: the closure must keep them alive for every
        // handshake this transport runs (a disposed cert breaks the chain build).
        var root = X509Certificate2.CreateFromPem(rootPem);
        var nodeAuthority = X509Certificate2.CreateFromPem(nodeAuthorityPem);
        return (_, certificate, _, _) =>
        {
            if (certificate is null) return false;
            using var candidate = new X509Certificate2(certificate);
            using var validator = new X509Chain();
            validator.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            validator.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            validator.ChainPolicy.CustomTrustStore.Add(root);
            validator.ChainPolicy.ExtraStore.Add(nodeAuthority);
            return validator.Build(candidate);
        };
    }
}

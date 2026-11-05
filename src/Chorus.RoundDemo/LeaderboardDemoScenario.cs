using Chorus.ControlServer;
using Chorus.Rpc;
using Npgsql;

namespace Chorus.RoundDemo;

/// <summary>
/// FIX-RANK-09: the leaderboard example journey's outcome (developer-capabilities.md
/// 「Deliver a default PostgreSQL provider, migrations and one runnable example covering
/// scoring, TopN, personal rank, duplicate settlement and period rollover with
/// historical-board queries」). Each field is one assertion the shared scenario holds, so the
/// console demo and the test suite assert the identical example.
/// </summary>
public sealed record LeaderboardDemoEvidence(
    string RoomId,
    int ProjectedEffects,
    long RedScore,
    long BlueScore,
    int TopEntries,
    string TopSubject,
    long TopScore,
    int RedRank,
    int BlueRank,
    bool DuplicateSettled,
    long RedAfterDuplicate,
    bool NonScoreRefused,
    long HistoricalRedScore,
    int HistoricalEntries,
    long StreamerTopScore,
    int StreamerTopEntries);

/// <summary>
/// FIX-RANK-09: the runnable leaderboard example (developer-capabilities.md capability line
/// 21). The journey first runs the real gameplay round (which already commits its score
/// effects to the committed effect ledger), then drives the ControlServer business modules
/// over the data host's authorized gRPC face: the real round's score effects are projected
/// onto the declared board, and a dedicated <c>demo-board</c> scope carries a deterministic
/// effect set that demonstrates TopN, personal rank, duplicate settlement (a re-projected
/// effect answers the stored score and never double-scores), source validation (a committed
/// non-score effect is refused) and period rollover with a historical-board read. The client
/// access is the same board read through a second, tenant-scoped <c>streamer-server</c>
/// identity — the authorized query face the capability declares, no new RPC.
/// </summary>
public static class LeaderboardDemoScenario
{
    public const string TenantId = "tenant-a";
    public const string GameId = RoundDemoScenario.GameId;

    /// <summary>The deterministic block's board scope: a room key distinct from the round's
    /// real scope (<see cref="RoundDemoScenario.RoomId"/>), so the synthetic effects never
    /// interfere with the round's own scores.</summary>
    private const string DemoScope = "demo-board";

    /// <summary>The example family's daily board (FIX-RANK-07) — the only family that
    /// demonstrates period rollover; the AllTime family is covered by its own tests.</summary>
    private const string BoardId = "faction-score-daily";

    public static async Task<LeaderboardDemoEvidence> RunAsync(
        IDemoCluster cluster, TextWriter log, CancellationToken cancellationToken = default)
    {
        // 1. The real gameplay journey: the round runs to settlement and its score effects are
        // already committed to the effect ledger (the frame commit writes them, RANK-02/03).
        var round = await RoundDemoScenario.RunAsync(cluster, log, cancellationToken: cancellationToken);
        await log.WriteLineAsync($"leaderboard example: round {round.RoomId} settled, effects projected next");

        // 2. The production composition: the business modules ride the data host's authorized
        // leaderboard gRPC face (control-server role, cluster identity naming the tenant).
        using var controlCertificate = cluster.Certificate("@cluster", "control-server", "demo-leaderboard");
        using var controlChannel = cluster.Channel(cluster.DataPort, controlCertificate, "data-server");
        var client = new Leaderboard.LeaderboardClient(controlChannel);
        var policy = new LeaderboardDailyFactionScoringPolicy();
        var scoring = new LeaderboardScoringService(new GrpcLeaderboardScoringDataPlane(client), policy);
        var query = new LeaderboardQueryService(new GrpcLeaderboardQueryDataPlane(client));

        // 3. Gameplay access: the round's own committed score effects are projected onto the
        // declared board. Only the count is asserted — a round that crossed a UTC midnight
        // splits its scores across two daily buckets, so an exact value would be a flake.
        var roundEffects = await ReadScoreEffectsAsync(cluster.DatabaseConnectionString,
            round.RoomId, cancellationToken);
        var projected = 0;
        foreach (var effect in roundEffects)
        {
            await scoring.ProjectScoreEffectAsync(TenantId, GameId, effect.EffectId, cancellationToken);
            projected++;
        }
        await log.WriteLineAsync($"leaderboard example: {projected} round score effects projected");

        // 4. The deterministic block on the dedicated scope (a different board key than the
        // round's, zero interference). The now-dated effects always land in today's UTC bucket
        // and the yesterday-noon effect always lands in yesterday's — no midnight race.
        var now = DateTimeOffset.UtcNow;
        var todayStart = new DateTimeOffset(now.Year, now.Month, now.Day, 0, 0, 0, TimeSpan.Zero);
        var yesterdayNoonUtc = todayStart.AddDays(-1).AddHours(12);

        await InsertCommittedEffectAsync(cluster.DatabaseConnectionString,
            $"{DemoScope}/0/0", "score", """{"faction":"red","delta":40,"total":40}""", now, cancellationToken);
        await InsertCommittedEffectAsync(cluster.DatabaseConnectionString,
            $"{DemoScope}/0/1", "score", """{"faction":"blue","delta":25,"total":25}""", now, cancellationToken);
        await InsertCommittedEffectAsync(cluster.DatabaseConnectionString,
            $"{DemoScope}/0/2", "score", """{"faction":"red","delta":35,"total":75}""", now, cancellationToken);

        await scoring.ProjectScoreEffectAsync(TenantId, GameId, $"{DemoScope}/0/0", cancellationToken);
        await scoring.ProjectScoreEffectAsync(TenantId, GameId, $"{DemoScope}/0/1", cancellationToken);
        var redAfter = await scoring.ProjectScoreEffectAsync(TenantId, GameId, $"{DemoScope}/0/2", cancellationToken);
        var todayBoard = policy.ResolveBoard(TenantId, GameId, $"{DemoScope}/0/0",
            policy.ResolvePeriod(now, now));

        // Duplicate settlement: re-projecting the first effect answers the already-applied
        // row as a duplicate — and the board still shows red at 75, never 115.
        var duplicate = await scoring.ProjectScoreEffectAsync(TenantId, GameId, $"{DemoScope}/0/0", cancellationToken);
        var afterDuplicate = await query.GetTopAsync(TenantId, todayBoard, 100, cancellationToken: cancellationToken);

        // TopN and personal rank on one read view (RANK-04/05).
        var top = await query.GetTopAsync(TenantId, todayBoard, 100, cancellationToken: cancellationToken);
        var redRank = await query.GetRankAsync(TenantId, todayBoard, "red", cancellationToken);
        var blueRank = await query.GetRankAsync(TenantId, todayBoard, "blue", cancellationToken);

        // Source validation: a committed non-score effect never enters a board (RANK-03).
        await InsertCommittedEffectAsync(cluster.DatabaseConnectionString,
            $"{DemoScope}/9/0", "round.started", """{"roomId":"demo-board"}""", now, cancellationToken);
        var nonScoreRefused = false;
        try
        {
            await scoring.ProjectScoreEffectAsync(TenantId, GameId, $"{DemoScope}/9/0", cancellationToken);
        }
        catch (LeaderboardRejectedException)
        {
            nonScoreRefused = true;
        }

        // Period rollover: the yesterday-noon effect belongs to yesterday's bucket (always
        // inside the 48h late window) and reads back from the historical board (RANK-07).
        await InsertCommittedEffectAsync(cluster.DatabaseConnectionString,
            $"{DemoScope}/1/0", "score", """{"faction":"red","delta":50,"total":50}""", yesterdayNoonUtc, cancellationToken);
        await scoring.ProjectScoreEffectAsync(TenantId, GameId, $"{DemoScope}/1/0", cancellationToken);
        var yesterdayBoard = policy.ResolveBoard(TenantId, GameId, $"{DemoScope}/1/0",
            policy.ResolvePeriod(yesterdayNoonUtc, DateTimeOffset.UtcNow));
        var historical = await query.GetTopAsync(TenantId, yesterdayBoard, 100, cancellationToken: cancellationToken);

        // Client access: the same board read through a tenant-scoped streamer-server identity
        // (the capability's authorized query face — the existing DataServer RPC, no new one).
        using var streamerCertificate = cluster.Certificate("tenant-a", "streamer-server", "demo-leaderboard-client");
        using var streamerChannel = cluster.Channel(cluster.DataPort, streamerCertificate, "data-server");
        var streamerQuery = new LeaderboardQueryService(
            new GrpcLeaderboardQueryDataPlane(new Leaderboard.LeaderboardClient(streamerChannel)));
        var streamerTop = await streamerQuery.GetTopAsync(TenantId, todayBoard, 100, cancellationToken: cancellationToken);

        var evidence = new LeaderboardDemoEvidence(
            RoomId: round.RoomId,
            ProjectedEffects: projected,
            RedScore: redAfter.Score,
            BlueScore: blueRank!.Entry.Score,
            TopEntries: top.Entries.Count,
            TopSubject: top.Entries[0].SubjectId,
            TopScore: top.Entries[0].Score,
            RedRank: redRank!.Rank,
            BlueRank: blueRank.Rank,
            DuplicateSettled: duplicate.Duplicate,
            RedAfterDuplicate: afterDuplicate.Entries.Single(entry => entry.SubjectId == "red").Score,
            NonScoreRefused: nonScoreRefused,
            HistoricalRedScore: historical.Entries[0].Score,
            HistoricalEntries: historical.Entries.Count,
            StreamerTopScore: streamerTop.Entries[0].Score,
            StreamerTopEntries: streamerTop.Entries.Count);
        await log.WriteLineAsync(
            $"leaderboard: today {evidence.RedScore} red / {evidence.BlueScore} blue on {DemoScope}, " +
            $"top {evidence.TopSubject}={evidence.TopScore}, ranks {evidence.RedRank}/{evidence.BlueRank}, " +
            $"duplicate {evidence.DuplicateSettled}, historical {evidence.HistoricalRedScore}, " +
            $"client {evidence.StreamerTopScore}");
        return evidence;
    }

    /// <summary>Reads the committed score effects of one room from the effect ledger, in the
    /// order the round committed them (the source order a projection runner would follow).</summary>
    private static async Task<List<(string EffectId, DateTimeOffset CommittedAt)>> ReadScoreEffectsAsync(
        string connectionString, string roomId, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var read = new NpgsqlCommand("""
            SELECT effect_id,committed_at FROM infra.outbox
            WHERE kind='score' AND effect_id LIKE @room || '/%'
            ORDER BY committed_at,effect_id
            """, connection);
        read.Parameters.AddWithValue("room", roomId);
        await using var reader = await read.ExecuteReaderAsync(cancellationToken);
        var effects = new List<(string, DateTimeOffset)>();
        while (await reader.ReadAsync(cancellationToken))
            effects.Add((reader.GetString(0), reader.GetFieldValue<DateTimeOffset>(1)));
        return effects;
    }

    /// <summary>Inserts one committed effect with an explicit source time — the deterministic
    /// block's effects carry the timestamp that decides their period bucket (RANK-07).</summary>
    private static async Task InsertCommittedEffectAsync(string connectionString, string effectId,
        string kind, string payloadJson, DateTimeOffset committedAt, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var insert = new NpgsqlCommand("""
            INSERT INTO infra.outbox(effect_id,kind,payload,status,committed_at)
            VALUES (@effect,@kind,@payload::jsonb,'Pending',@committed_at)
            ON CONFLICT (effect_id) DO NOTHING
            """, connection);
        insert.Parameters.AddWithValue("effect", effectId);
        insert.Parameters.AddWithValue("kind", kind);
        insert.Parameters.AddWithValue("payload", payloadJson);
        insert.Parameters.AddWithValue("committed_at", committedAt);
        await insert.ExecuteNonQueryAsync(cancellationToken);
    }
}

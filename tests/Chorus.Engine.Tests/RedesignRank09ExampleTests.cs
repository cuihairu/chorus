using Chorus.RoundDemo;
using Npgsql;
using Xunit;

namespace Chorus.Engine.Tests;

/// <summary>
/// FIX-RANK-09, the runnable leaderboard example (developer-capabilities.md 「Deliver a
/// default PostgreSQL provider, migrations and one runnable example covering scoring, TopN,
/// personal rank, duplicate settlement and period rollover with historical-board queries」):
/// the identical scenario the standalone Chorus.RoundDemo console runs, over the shared host
/// environment. The round's committed score effects project onto the declared board, and the
/// deterministic <c>demo-board</c> block demonstrates TopN, personal rank, duplicate
/// settlement (never a double-score), source validation (a non-score effect is refused) and
/// period rollover with a historical-board read, all through the authorized gRPC query face
/// (a control-server read and a tenant-scoped streamer-server read).
/// </summary>
public sealed class RedesignRank09ExampleTests
{
    [Fact]
    public async Task TheExampleJourneyProjectsAndAnswersEveryDeclaredScenario()
    {
        await using var environment = await RedesignM0HostTests.HostEnvironment.CreateAsync();
        using var cluster = new RoundDemoScenarioTests.HostEnvironmentCluster(environment);
        var evidence = await LeaderboardDemoScenario.RunAsync(cluster, Console.Out);

        // The gameplay access: the round's own committed score effects were projected (only a
        // count is asserted — a round that crossed a UTC midnight splits across two buckets).
        Assert.True(evidence.ProjectedEffects >= 2);

        // The deterministic block: today's board is red 75 / blue 25 on the demo scope.
        Assert.Equal(75L, evidence.RedScore);
        Assert.Equal(25L, evidence.BlueScore);

        // TopN and personal rank on one read view.
        Assert.Equal(2, evidence.TopEntries);
        Assert.Equal("red", evidence.TopSubject);
        Assert.Equal(75L, evidence.TopScore);
        Assert.Equal(1, evidence.RedRank);
        Assert.Equal(2, evidence.BlueRank);

        // Duplicate settlement: re-projecting a settled effect answers a duplicate and never
        // double-scores — the board still shows red at 75.
        Assert.True(evidence.DuplicateSettled);
        Assert.Equal(75L, evidence.RedAfterDuplicate);

        // Source validation: a committed non-score effect never enters a board.
        Assert.True(evidence.NonScoreRefused);

        // Period rollover: the yesterday-noon effect landed in yesterday's bucket and reads
        // back from the historical board (one entry, red 50), isolated from today's board.
        Assert.Equal(50L, evidence.HistoricalRedScore);
        Assert.Equal(1, evidence.HistoricalEntries);

        // Client access: the same board answers identically through the tenant-scoped
        // streamer-server identity — the authorized query face, no new RPC.
        Assert.Equal(75L, evidence.StreamerTopScore);
        Assert.Equal(2, evidence.StreamerTopEntries);

        // The storage floor agrees: the demo scope holds exactly two daily buckets — today's
        // (red 75, blue 25) and yesterday's (red 50) — with no leakage between them.
        await AssertBucketIsolationAsync(environment.DatabaseConnectionString);
    }

    /// <summary>Probes the stored projection directly: the demo scope has exactly two
    /// consecutive daily buckets, one carrying today's red 75 / blue 25 and the other
    /// carrying yesterday's red 50 alone. No period id is computed here — the buckets are
    /// read as stored, so the assertion is immune to a UTC-midnight test run.</summary>
    private static async Task AssertBucketIsolationAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var read = new NpgsqlCommand("""
            SELECT period_id,subject_id,score FROM game.leaderboard_entry
            WHERE board_id='faction-score-daily' AND scope_id='demo-board'
            ORDER BY period_id,subject_id
            """, connection);
        await using var reader = await read.ExecuteReaderAsync();
        var rows = new List<(string Period, string Subject, long Score)>();
        while (await reader.ReadAsync())
            rows.Add((reader.GetString(0), reader.GetString(1), reader.GetInt64(2)));

        Assert.Equal(3, rows.Count); // 75 + 25 today, 50 yesterday — never re-amounted.
        var buckets = rows.GroupBy(row => row.Period).ToList();
        Assert.Equal(2, buckets.Count); // two consecutive daily buckets, no leakage.
        foreach (var bucket in buckets)
        {
            var entries = bucket.ToList();
            if (entries.Count == 2)
            {
                Assert.Contains(entries, entry => entry.Subject == "red" && entry.Score == 75);
                Assert.Contains(entries, entry => entry.Subject == "blue" && entry.Score == 25);
            }
            else
            {
                var entry = Assert.Single(entries);
                Assert.Equal(("red", 50L), (entry.Subject, entry.Score));
            }
        }
    }
}

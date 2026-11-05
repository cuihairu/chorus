using Chorus.RoundDemo;

// The standalone round demo: a whole cluster on localhost, one authoritative round, two
// streamer edges, one client command, settlement and a leaderboard — the same journey the
// test suite runs (RoundDemoScenarioTests), printed as it happens. The round's committed
// score effects then drive the leaderboard example (FIX-RANK-09): the business modules
// project them onto the declared board and the example journey (scoring, TopN, personal
// rank, duplicate settlement, period rollover with a historical-board read, and the
// authorized client access) is printed as it happens.
if (Environment.GetEnvironmentVariable("CHORUS_TEST_POSTGRES") is null)
{
    Console.Error.WriteLine(
        "Set CHORUS_TEST_POSTGRES to a PostgreSQL connection string whose role may create databases.");
    return 1;
}
await using var cluster = new DemoCluster();
try
{
    // The example journey runs the round internally (RoundDemoScenario logs it as it happens),
    // then projects the round's committed score effects and walks the deterministic example.
    var leaderboard = await LeaderboardDemoScenario.RunAsync(cluster, Console.Out);
    Console.WriteLine($"leaderboard example: today {leaderboard.RedScore} red / {leaderboard.BlueScore} blue, " +
        $"top {leaderboard.TopSubject}={leaderboard.TopScore} of {leaderboard.TopEntries}, " +
        $"ranks {leaderboard.RedRank}/{leaderboard.BlueRank}, duplicate settled={leaderboard.DuplicateSettled}, " +
        $"historical {leaderboard.HistoricalRedScore}, client read {leaderboard.StreamerTopEntries} entries.");
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine("the round demo failed: " + error);
    return 1;
}

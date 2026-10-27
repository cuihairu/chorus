using Chorus.RoundDemo;

// The standalone round demo: a whole cluster on localhost, one authoritative round, two
// streamer edges, one client command, settlement and a leaderboard — the same journey the
// test suite runs (RoundDemoScenarioTests), printed as it happens.
if (Environment.GetEnvironmentVariable("CHORUS_TEST_POSTGRES") is null)
{
    Console.Error.WriteLine(
        "Set CHORUS_TEST_POSTGRES to a PostgreSQL connection string whose role may create databases.");
    return 1;
}
await using var cluster = new DemoCluster();
try
{
    var evidence = await RoundDemoScenario.RunAsync(cluster, Console.Out);
    Console.WriteLine($"round {evidence.RoomId} {evidence.RoomStatus.ToLowerInvariant()} " +
        $"at epoch {evidence.Epoch}: both edges hold tick {evidence.Host.FinalTick}.");
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine("the round demo failed: " + error);
    return 1;
}

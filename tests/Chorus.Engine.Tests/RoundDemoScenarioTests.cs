using System.Text.Json;
using Chorus.RoundDemo;

namespace Chorus.Engine.Tests;

/// <summary>
/// The round demo journey under the shared scenario: the identical code the standalone
/// Chorus.RoundDemo console runs, here over the test host environment (M2-6b). Two streamers
/// mount, one round runs to its frozen result on both edges, the client command receipt is
/// part of the committed record, and the settlement lands the leaderboard.
/// </summary>
public sealed class RoundDemoScenarioTests
{
    [Fact]
    public async Task TwoStreamersRunTheDemoJourneyToSettlementAndTheLeaderboard()
    {
        await using var environment = await RedesignM0HostTests.HostEnvironment.CreateAsync();
        var evidence = await RoundDemoScenario.RunAsync(new HostEnvironmentCluster(environment), Console.Out);

        // Both edges hold the same committed record: a snapshot before any delta, a
        // contiguous delta chain (the scenario enforces it while reading) and the frozen
        // result of the final tick (AT15).
        Assert.Equal(120L, evidence.Host.FinalTick);
        Assert.Equal(evidence.Host.FinalTick, evidence.Guest.FinalTick);
        Assert.Equal(evidence.Host.FinalVersion, evidence.Guest.FinalVersion);
        Assert.Equal(evidence.Host.ResultJson, evidence.Guest.ResultJson);
        Assert.True(evidence.Host.Snapshots >= 1);
        Assert.True(evidence.Host.Deltas >= 1);
        Assert.True(evidence.Guest.Snapshots >= 1);

        // The client command is part of the authoritative frame: every subscriber sees its
        // receipt exactly once, not just the edge that sent it (6.3, 8.3).
        Assert.Equal(new[] { RoundDemoScenario.BoostCommandId }, evidence.Host.CommandReceipts);
        Assert.Equal(new[] { RoundDemoScenario.BoostCommandId }, evidence.Guest.CommandReceipts);

        // The frozen ranking: the routed gifts plus the boost credited to the host's faction.
        using (var document = JsonDocument.Parse(evidence.Host.ResultJson))
        {
            var scores = new Dictionary<string, long>();
            foreach (var entry in document.RootElement.GetProperty("ranking").EnumerateArray())
                scores[entry.GetProperty("faction").GetString()!] = entry.GetProperty("score").GetInt64();
            Assert.InRange(scores["red"], 130, 134);
            Assert.Equal(30L, scores["blue"]);
        }

        // Settlement projected the same outcome onto the leaderboard and the persistent
        // terminator closed the round (AT18, AT20).
        Assert.Equal("Settled", evidence.RoomStatus);
        Assert.Equal(2, evidence.Leaderboard.Count);
        Assert.Equal(evidence.Host.StreamerId, evidence.Leaderboard[0].PlayerId);
        Assert.InRange(evidence.Leaderboard[0].Score, 130, 134);
        Assert.Equal(evidence.Guest.StreamerId, evidence.Leaderboard[1].PlayerId);
        Assert.Equal(30L, evidence.Leaderboard[1].Score);
    }

    /// <summary>
    /// The demo cluster contract over the shared host environment: the same four real hosts,
    /// certificates and ports the other host tests use, so the scenario code path is the
    /// console demo's code path. Disposal stays with the owning test.
    /// </summary>
    private sealed class HostEnvironmentCluster : IDemoCluster
    {
        private readonly RedesignM0HostTests.HostEnvironment _environment;

        public HostEnvironmentCluster(RedesignM0HostTests.HostEnvironment environment) =>
            _environment = environment;

        public int ControlPort => _environment.ControlPort;
        public int DataPort => _environment.DataPort;
        public int StreamerPort => _environment.StreamerPort;
        public int GamePort => _environment.GamePort;
        public string DatabaseConnectionString => _environment.DatabaseConnectionString;

        public async Task StartAllAsync(byte[] simulatorSigningKey, byte[] accessCredentialKey)
        {
            var streamerEnvironment = new Dictionary<string, string>
            {
                ["CHORUS_DATA_PORT"] = DataPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["CHORUS_PLATFORM_SIMULATOR"] = "true",
                ["CHORUS_SIMULATOR_SIGNING_KEY"] = Convert.ToHexString(simulatorSigningKey),
                ["CHORUS_ACCESS_CREDENTIAL_KEY"] = Convert.ToHexString(accessCredentialKey),
                ["CHORUS_TENANT_APPLICATIONS"] = """{"tenant-a":{"app-1":"game-a"}}"""
            };
            await _environment.StartAsync("Chorus.ControlServer", ControlPort, "control-server");
            await _environment.StartAsync("Chorus.DataServer", DataPort, "data-server");
            await _environment.StartAsync("Chorus.StreamerServer", StreamerPort,
                "streamer-server", streamerEnvironment);
            await _environment.StartAsync("Chorus.GameServer", GamePort, "game-server",
                new Dictionary<string, string>
                {
                    ["CHORUS_DATA_PORT"] = DataPort.ToString(System.Globalization.CultureInfo.InvariantCulture)
                },
                clientTenant: "tenant-a");
        }

        public System.Security.Cryptography.X509Certificates.X509Certificate2 Certificate(
            string tenant, string role, string instance) => _environment.Certificate(tenant, role, instance);

        public Grpc.Net.Client.GrpcChannel Channel(
            int port, System.Security.Cryptography.X509Certificates.X509Certificate2 certificate, string role) =>
            _environment.Channel(port, certificate, role);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

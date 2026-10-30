using System.Security.Cryptography.X509Certificates;
using Chorus.ControlServer;
using Chorus.Redesign.Rpc;

namespace Chorus.Engine.Tests;

/// <summary>The game-host half of the ClusterDirectory (FIX-SCHED-01): seeded at construction,
/// maintained from valid process reports — discovery, restart address replacement, staleness
/// eviction. Channels are created lazily by the client factory; no connection is made here.</summary>
public sealed class RedesignClusterDirectoryUnitTests
{
    private sealed class Certificates : IDisposable
    {
        public X509Certificate2 Client { get; }
        public X509Certificate2 Root { get; }
        public X509Certificate2 Authority { get; }

        public Certificates()
        {
            Root = ProcessCertificateIssuer.CreateRoot("cluster-a", DateTimeOffset.UtcNow.AddDays(1));
            Authority = ProcessCertificateIssuer.CreateNodeAuthority(Root,
                new("cluster-a", "node-a",
                    ["node-agent", "control-server", "data-server", "streamer-server", "game-server"],
                    ["@cluster", "tenant-a"]),
                DateTimeOffset.UtcNow.AddDays(1));
            Client = ProcessCertificateIssuer.CreateProcess(Authority,
                new("cluster-a", "@cluster", "node-a", "control-server", "control-unit"),
                DateTimeOffset.UtcNow.AddDays(1), "localhost");
        }

        public void Dispose()
        {
            Client.Dispose();
            Authority.Dispose();
            Root.Dispose();
        }
    }

    private static ClusterDirectory Directory(Certificates certificates,
        string[] seeds, TimeSpan staleAfter) => new(
        certificates.Client, certificates.Root, certificates.Authority, "cluster-a",
        "https://localhost:7501", seeds, staleAfter);

    private static GameHostReport Report(string instanceId, string endpoint, DateTime observedAtUtc) =>
        new(instanceId, endpoint, observedAtUtc, "Ready", "Open");

    [Fact]
    public void SeedsPopulateTheDirectoryInIdOrder()
    {
        using var certificates = new Certificates();
        var directory = Directory(certificates,
            ["game-b=https://localhost:2001", "game-a=https://localhost:1001"],
            TimeSpan.FromSeconds(60));
        Assert.Equal(2, directory.GameHostCount);
        // Deterministic id order: the round-robin spread and the recovery candidates stay
        // stable no matter in which order the configuration listed the hosts.
        Assert.Equal(new[] { "game-a", "game-b" }, directory.GameHostIds);
        Assert.Equal("https://localhost:1001", directory.GameHostEndpoint("game-a"));
        Assert.NotNull(directory.GameHostChannel("game-a"));
        var picked = new HashSet<string>();
        for (var index = 0; index < 4; index++)
            picked.Add(directory.NextGameHost().InstanceId);
        Assert.True(picked.SetEquals(["game-a", "game-b"]));
    }

    [Fact]
    public void ReportsDiscoverHostsWithoutASeed()
    {
        using var certificates = new Certificates();
        var directory = Directory(certificates, [], TimeSpan.FromSeconds(60));
        Assert.Equal(0, directory.GameHostCount);
        var now = DateTime.UtcNow;
        directory.ApplyReports([Report("game-new", "https://localhost:3003", now)], now);
        Assert.Equal(1, directory.GameHostCount);
        Assert.Equal("https://localhost:3003", directory.GameHostEndpoint("game-new"));
        Assert.Same(directory.GameHostChannel("game-new"), directory.NextGameHost().Channel);
    }

    [Fact]
    public void ARestartedInstanceServesAtItsNewAddress()
    {
        using var certificates = new Certificates();
        var directory = Directory(certificates, ["game-a=https://localhost:1001"],
            TimeSpan.FromSeconds(60));
        var now = DateTime.UtcNow;
        directory.ApplyReports([Report("game-a", "https://localhost:4004", now)], now);
        Assert.Equal("https://localhost:4004", directory.GameHostEndpoint("game-a"));
        Assert.Equal("game-a", directory.NextGameHost().InstanceId);
    }

    [Fact]
    public void SilentHostsAgeOutOfTheDirectory()
    {
        using var certificates = new Certificates();
        var directory = Directory(certificates, ["game-a=https://localhost:1001"],
            TimeSpan.FromSeconds(10));
        var now = DateTime.UtcNow;
        directory.ApplyReports([], now.AddSeconds(11));
        Assert.Equal(0, directory.GameHostCount);
        Assert.Empty(directory.GameHostIds);
        Assert.Null(directory.GameHostChannel("game-a"));
        Assert.Null(directory.GameHostEndpoint("game-a"));
        Assert.Throws<InvalidOperationException>(() => directory.NextGameHost());
    }

    [Fact]
    public void FreshReportsKeepAHostPastItsSeedEvidence()
    {
        using var certificates = new Certificates();
        var directory = Directory(certificates, ["game-a=https://localhost:1001"],
            TimeSpan.FromSeconds(10));
        var now = DateTime.UtcNow;
        // The seed's evidence is the construction time; a report re-anchors it, so the host
        // outlives the seed window for as long as it keeps reporting.
        directory.ApplyReports([Report("game-a", "https://localhost:1001", now.AddSeconds(9))],
            now.AddSeconds(11));
        Assert.Equal(1, directory.GameHostCount);
        Assert.Equal("game-a", directory.GameHostIds.Single());
    }

    [Fact]
    public void AStaleReportEvictsItsOwnHost()
    {
        using var certificates = new Certificates();
        var directory = Directory(certificates, [], TimeSpan.FromSeconds(10));
        var now = DateTime.UtcNow;
        directory.ApplyReports([Report("game-a", "https://localhost:1001", now)], now);
        Assert.Equal(1, directory.GameHostCount);
        // The next refresh folds only fresh evidence; a report older than the gate — here the
        // same report seen past its window — no longer keeps the host assignable.
        directory.ApplyReports([Report("game-a", "https://localhost:1001", now)], now.AddSeconds(11));
        Assert.Equal(0, directory.GameHostCount);
    }

    [Fact]
    public void MalformedReportsChangeNothing()
    {
        using var certificates = new Certificates();
        var directory = Directory(certificates, ["game-a=https://localhost:1001"],
            TimeSpan.FromSeconds(60));
        var now = DateTime.UtcNow;
        directory.ApplyReports(
            [Report("", "https://localhost:5", now), Report("game-b", "", now)], now);
        Assert.Equal(new[] { "game-a" }, directory.GameHostIds);
    }
}

using System.Security.Cryptography.X509Certificates;
using Chorus.Redesign.Rpc;
using Grpc.Net.Client;

namespace Chorus.ControlServer;

/// <summary>One live game host as the control plane knows it: where it serves, when that
/// was last proven (FIX-SCHED-01), and whether it wants new rooms (FIX-SCHED-02) — the
/// readiness and admission the report carried. The evidence clock is the process report's
/// observed_at — a configured seed carries its construction time until a report takes the
/// entry over.</summary>
public sealed record GameHostReport(
    string InstanceId, string Endpoint, DateTime ObservedAtUtc, string Readiness, string Admission);

/// <summary>
/// The control plane's outbound channel directory (architecture 7.2): pooled, mutually
/// authenticated channels to the data plane and the game hosts. The data channel is fixed
/// configuration; the game-host half is maintained from valid process reports (FIX-SCHED-01):
/// a refresh loop feeds <see cref="ApplyReports"/> with the live game-server reports, new and
/// restarted instances join without a ControlServer restart, and an instance whose last
/// evidence ages past the staleness gate leaves the directory so its address is never used
/// for a new assignment. Configured endpoints remain the bootstrap seed — they hold their
/// construction-time evidence and expire under the same gate unless reports keep them alive.
/// Membership and eligibility are separate answers (FIX-SCHED-02): every reachable host —
/// drained, full, not ready — stays in the directory so its existing rooms keep a channel,
/// while <see cref="NextGameHost"/> and <see cref="EligibleGameHostCount"/> see only hosts
/// whose evidence is fresh, whose readiness is Ready and whose admission takes new rooms.
/// Each entry names the host instance and where it serves — "id=endpoint" pairs, or a bare
/// endpoint whose address doubles as the instance id (M3-4 convention). The instance id is
/// what the coordinator writes into ownership and commit fences; the self-reported routing
/// snapshot (M3-5) joins it back to the endpoint the host serves on.
/// </summary>
public sealed class ClusterDirectory
{
    /// <summary>How long the last evidence keeps a host in the directory. Long enough that a
    /// report cycle plus a refresh pass never evicts a healthy host; short enough that a dead
    /// host stops receiving assignments.</summary>
    public static readonly TimeSpan DefaultStaleAfter = TimeSpan.FromSeconds(60);

    private readonly X509Certificate2 _clientCertificate;
    private readonly X509Certificate2 _root;
    private readonly X509Certificate2 _nodeAuthority;
    private readonly string _clusterId;
    private readonly TimeSpan _staleAfter;

    public GrpcChannel DataChannel { get; }

    // Channels pooled per endpoint: a restarted instance on a new address gets a new channel,
    // while the unchanged majority keeps theirs. Superseded channels are few and bounded by
    // restart count — they are not disposed mid-flight, matching the directory's own lifetime.
    private readonly object _channelLock = new();
    private readonly Dictionary<string, GrpcChannel> _channelsByEndpoint = new(StringComparer.Ordinal);

    // Immutable snapshot: ApplyReports rebuilds and publishes a whole new array, so readers
    // (pairing, takeover, sweeps) resolve hosts and ids without locks and never see a half
    // of two worlds.
    private volatile GameHostEntry[] _hosts;
    private int _cursor;

    /// <summary>Test seam: the selection-time freshness check reads this clock, so a unit
    /// test can age evidence past the staleness gate without sleeping.</summary>
    internal Func<DateTime> UtcNow { get; set; } = static () => DateTime.UtcNow;

    private sealed record GameHostEntry(
        string InstanceId, string Endpoint, GrpcChannel Channel, DateTime LastEvidenceUtc,
        string Readiness, string Admission);

    public ClusterDirectory(X509Certificate2 clientCertificate, X509Certificate2 root,
        X509Certificate2 nodeAuthority, string clusterId, string dataEndpoint,
        IReadOnlyList<string> gameEndpoints, TimeSpan? staleAfter = null)
    {
        _clientCertificate = clientCertificate;
        _root = root;
        _nodeAuthority = nodeAuthority;
        _clusterId = clusterId;
        _staleAfter = staleAfter ?? DefaultStaleAfter;
        DataChannel = ProcessGrpcClientFactory.Create(
            new Uri(dataEndpoint), clientCertificate, root, nodeAuthority, clusterId, "data-server");
        _hosts = gameEndpoints
            .Select(entry =>
            {
                var separator = entry.IndexOf('=');
                // "game-a=https://host:port" names the instance; a bare endpoint is its own id.
                var (instanceId, endpoint) = separator < 0
                    ? (entry, entry)
                    : (entry[..separator], entry[(separator + 1)..]);
                // A configured seed is trusted to admit until a report says otherwise.
                return new GameHostEntry(instanceId, endpoint, ChannelFor(endpoint), DateTime.UtcNow,
                    "Ready", ProcessAdmission.Accepting);
            })
            .OrderBy(host => host.InstanceId, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>Folds a fresh batch of game-host reports into the directory: new instances
    /// join, a restarted instance's new address replaces its old one, and every entry whose
    /// last evidence (report or seed construction) is older than the staleness gate —
    /// including entries absent from this batch — leaves. Readiness and admission ride with
    /// the report (FIX-SCHED-02): the entry keeps them for the scheduler, and no value of
    /// either removes the host — an unreachable host leaves by evidence, an unwanted one by
    /// eligibility.</summary>
    public void ApplyReports(IReadOnlyList<GameHostReport> reports, DateTime nowUtc)
    {
        var merged = _hosts.ToDictionary(host => host.InstanceId, StringComparer.Ordinal);
        foreach (var report in reports)
        {
            if (string.IsNullOrWhiteSpace(report.InstanceId) || string.IsNullOrWhiteSpace(report.Endpoint))
                continue;
            merged[report.InstanceId] = new GameHostEntry(
                report.InstanceId, report.Endpoint, ChannelFor(report.Endpoint), report.ObservedAtUtc,
                report.Readiness, report.Admission);
        }
        var cutoff = nowUtc - _staleAfter;
        _hosts = merged.Values
            .Where(host => host.LastEvidenceUtc >= cutoff)
            .OrderBy(host => host.InstanceId, StringComparer.Ordinal)
            .ToArray();
    }

    private GrpcChannel ChannelFor(string endpoint)
    {
        lock (_channelLock)
        {
            if (_channelsByEndpoint.TryGetValue(endpoint, out var channel)) return channel;
            return _channelsByEndpoint[endpoint] = ProcessGrpcClientFactory.Create(
                new Uri(endpoint), _clientCertificate, _root, _nodeAuthority, _clusterId, "game-server");
        }
    }

    /// <summary>The number of live game hosts — zero means the coordinator must not pair.</summary>
    public int GameHostCount => _hosts.Length;

    /// <summary>The live host instance ids — the recovery sweep's candidate list
    /// (FIX-TAKE-05): a timed-out recovery moves to one of these, never a caller's word.</summary>
    public IReadOnlyList<string> GameHostIds => _hosts.Select(host => host.InstanceId).ToArray();

    /// <summary>How many hosts may take a NEW room right now (FIX-SCHED-02): fresh evidence,
    /// Ready, admitting admission. Zero does not mean nobody is reachable — a fleet drained
    /// for maintenance still serves — it means pairing must wait.</summary>
    public int EligibleGameHostCount => EligibleHosts(UtcNow()).Length;

    /// <summary>Round-robin over the hosts that may take a new room (FIX-SCHED-02): fresh
    /// evidence, Ready, and admission that accepts — a NotReady, Full or Draining host is
    /// never offered one. Eligibility is judged at selection time too, not only at fold
    /// time: a refresh that silently stopped must not keep serving yesterday's evidence.</summary>
    public (string InstanceId, GrpcChannel Channel) NextGameHost()
    {
        var hosts = EligibleHosts(UtcNow());
        if (hosts.Length == 0)
            throw new InvalidOperationException(
                "No eligible game hosts are in the directory for the coordinator: every live host is " +
                "stale, not ready, full, or draining.");
        var host = hosts[Interlocked.Increment(ref _cursor) % hosts.Length];
        return (host.InstanceId, host.Channel);
    }

    private GameHostEntry[] EligibleHosts(DateTime nowUtc)
    {
        var cutoff = nowUtc - _staleAfter;
        return _hosts
            .Where(host => host.LastEvidenceUtc >= cutoff
                && string.Equals(host.Readiness, "Ready", StringComparison.Ordinal)
                && ProcessAdmission.AdmitsNewRooms(host.Admission))
            .ToArray();
    }

    /// <summary>The channel of one live host — a recovered room's owner must be reached
    /// where it lives, not wherever round-robin points next. Null when the owner is unknown
    /// to this directory or its evidence has gone stale.</summary>
    public GrpcChannel? GameHostChannel(string instanceId)
    {
        foreach (var host in _hosts)
            if (host.InstanceId == instanceId)
                return host.Channel;
        return null;
    }

    /// <summary>The address one live host serves on — the join the routing snapshot proves
    /// per report. Null when the instance is not in the directory.</summary>
    public string? GameHostEndpoint(string instanceId)
    {
        foreach (var host in _hosts)
            if (host.InstanceId == instanceId)
                return host.Endpoint;
        return null;
    }
}

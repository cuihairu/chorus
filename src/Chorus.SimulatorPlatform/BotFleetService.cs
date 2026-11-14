using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Chorus.Platform;
using Chorus.Rpc;
using Grpc.Net.Client;

namespace Chorus.SimulatorPlatform;

/// <summary>Everything one fleet run needs to reach the real chain: where the streamer
/// gateway and the ingress face live, the simulator's signing key, the mTLS material
/// every internal client dials with (the same root, node authority and cluster id the
/// ProcessGrpcClientFactory trust rules consume — the fleet adds no trust semantics of
/// its own), and the optional reconciliation target (room, faction, data-plane
/// connection). The faction names the script's side in the room; leave it null for a
/// delivery-only reconciliation.</summary>
public sealed record BotRunConfig(
    string StreamerEndpoint,
    string IngressBaseUrl,
    string AppId,
    string GameId,
    string SimulatorSigningKeyHex,
    string ClusterId,
    string ClientCertificatePemPath,
    string ClientKeyPemPath,
    string RootCaPemPath,
    string NodeAuthorityPemPath,
    string ServerRole = "streamer-gateway",
    int ViewerCount = 8,
    string? ReconcileRoomId = null,
    string? ReconcileFaction = null,
    string? ReconcileConnectionString = null);

/// <summary>One run as the panel sees it: the lifecycle state, when it started and
/// finished, the failure if it died, the reconciliation's deviations (empty is the
/// pass), and the runner's own counts once the schedule has begun.</summary>
public sealed record BotRunRecord(
    string Id, string ScenarioId, string State,
    DateTimeOffset? StartedAt, DateTimeOffset? FinishedAt,
    string? Error, IReadOnlyList<string> Deviations, FleetSnapshot? Snapshot);

/// <summary>
/// The bots fleet the console's BotsPage drives (BOTS-2): starts scripted runs against
/// the real chain — mount over the streamer gateway's gRPC face, play the schedule over
/// the ingress face, then hold the run to its word against the data plane. One run is
/// one task; the registry is in-memory because a run is ephemeral (scenarios live in
/// the control plane's records, runs do not). Parse and material loading happen before
/// the run spawns, so an operator's typo answers at the request, not mid-run.
/// </summary>
public sealed class BotFleetService
{
    /// <summary>The reconciliation's patience for the room's result to freeze.</summary>
    public static readonly TimeSpan ReconcileBudget = TimeSpan.FromSeconds(120);

    private const int KeepRuns = 200;

    private readonly object _lock = new();
    private readonly Dictionary<string, RunState> _runs = [];
    private long _sequence;

    private sealed class RunState
    {
        public required string Id;
        public long Number;
        public required string ScenarioId;
        public required BotRunConfig Config;
        public required BotScript Script;
        public required CancellationTokenSource Cancellation;
        public AudienceEventPoster? Poster;
        public BotFleetRunner? Runner;
        public BotStreamerHandle? Handle;
        public string State = "Starting";
        public DateTimeOffset? StartedAt;
        public DateTimeOffset? FinishedAt;
        public string? Error;
        public IReadOnlyList<string> Deviations = [];
        public FleetSnapshot? Snapshot;
        public GrpcChannel? Channel;
        public HttpClient? Http;
        public X509Certificate2? ClientCertificate;
        public X509Certificate2? Root;
        public X509Certificate2? NodeAuthority;

        public void Begin()
        {
            lock (this)
            {
                State = "Running";
                StartedAt = DateTimeOffset.UtcNow;
            }
        }

        public void Hold(string state, FleetSnapshot? snapshot, string? error,
            IReadOnlyList<string> deviations)
        {
            lock (this)
            {
                State = state;
                Snapshot = snapshot ?? Snapshot;
                Error = error;
                Deviations = deviations;
                FinishedAt = DateTimeOffset.UtcNow;
            }
        }

        public BotRunRecord Record()
        {
            lock (this)
            {
                return new BotRunRecord(Id, ScenarioId, State, StartedAt, FinishedAt,
                    Error, Deviations, Snapshot);
            }
        }
    }

    /// <summary>Starts a run of one scenario's script. Throws ArgumentException (the
    /// error middleware's 400) for an unparsable script, a malformed signing key, or
    /// missing trust material — the run only spawns once everything checked out.</summary>
    public BotRunRecord Start(string scenarioId, string toml, BotRunConfig config)
    {
        var script = BotScript.Parse(toml, config.ViewerCount);
        byte[] signingKey;
        try
        {
            signingKey = Convert.FromHexString(config.SimulatorSigningKeyHex);
        }
        catch (FormatException ex)
        {
            throw new ArgumentException("simulatorSigningKeyHex: " + ex.Message, ex);
        }
        if (signingKey.Length < 32)
            throw new ArgumentException("simulatorSigningKeyHex: the key must be at least 32 bytes");
        Uri endpoint;
        try
        {
            endpoint = new Uri(config.StreamerEndpoint);
        }
        catch (UriFormatException ex)
        {
            throw new ArgumentException("streamerEndpoint: " + ex.Message, ex);
        }
        if (endpoint.Scheme != Uri.UriSchemeHttps)
            throw new ArgumentException("streamerEndpoint: the streamer gateway dials over HTTPS");
        foreach (var (path, name) in new (string Path, string Name)[]
        {
            (config.ClientCertificatePemPath, "clientCertificatePemPath"),
            (config.ClientKeyPemPath, "clientKeyPemPath"),
            (config.RootCaPemPath, "rootCaPemPath"),
            (config.NodeAuthorityPemPath, "nodeAuthorityPemPath")
        })
            if (!File.Exists(path))
                throw new ArgumentException($"{name}: no file at {path}");
        X509Certificate2 clientCertificate, root, nodeAuthority;
        try
        {
            clientCertificate = X509Certificate2.CreateFromPemFile(
                config.ClientCertificatePemPath, config.ClientKeyPemPath);
            // Cert-only files go through the one-argument CreateFromPem — the two-argument
            // file helper with no key file pairs against an empty span and refuses.
            root = X509Certificate2.CreateFromPem(File.ReadAllText(config.RootCaPemPath));
            nodeAuthority = X509Certificate2.CreateFromPem(File.ReadAllText(config.NodeAuthorityPemPath));
        }
        catch (Exception ex) when (ex is CryptographicException or InvalidOperationException)
        {
            throw new ArgumentException("trust material: " + ex.Message, ex);
        }

        var run = new RunState
        {
            Id = "", ScenarioId = scenarioId, Config = config, Script = script,
            Cancellation = new CancellationTokenSource(),
            ClientCertificate = clientCertificate, Root = root, NodeAuthority = nodeAuthority
        };
        lock (_lock)
        {
            run.Number = ++_sequence;
            run.Id = "bots-run-" + run.Number;
            _runs[run.Id] = run;
            TrimLocked();
        }
        _ = Task.Run(() => ExecuteAsync(run, endpoint, signingKey));
        return run.Record();
    }

    /// <summary>Cancels a live run. Unknown ids answer 404 (KeyNotFoundException);
    /// already-terminal runs answer false — there is nothing left to stop.</summary>
    public bool Stop(string id)
    {
        lock (_lock)
        {
            if (!_runs.TryGetValue(id, out var run))
                throw new KeyNotFoundException("No bots run " + id);
            if (Terminal(run.State)) return false;
            run.Cancellation.Cancel();
            return true;
        }
    }

    public IReadOnlyList<BotRunRecord> Runs()
    {
        lock (_lock)
            return _runs.Values.OrderByDescending(r => r.Number).Select(r => r.Record()).ToList();
    }

    public BotRunRecord Run(string id)
    {
        lock (_lock)
            return _runs.TryGetValue(id, out var run)
                ? run.Record()
                : throw new KeyNotFoundException("No bots run " + id);
    }

    public static bool Terminal(string state) =>
        state is "Completed" or "Reconciled" or "Deviated" or "Failed" or "Stopped";

    private async Task ExecuteAsync(RunState run, Uri endpoint, byte[] signingKey)
    {
        try
        {
            // The same trust rules every internal client dials with; the run owns the
            // material loaded at Start and sheds it when the run ends.
            run.Channel = ProcessGrpcClientFactory.Create(endpoint, run.ClientCertificate!,
                run.Root!, run.NodeAuthority!, run.Config.ClusterId, run.Config.ServerRole);
            var session = new Chorus.Rpc.StreamerSession.StreamerSessionClient(run.Channel);
            var simulator = new DevelopmentPlatformSimulator(signingKey);
            var streamer = new BotStreamer(simulator, session, run.Config.AppId,
                run.Config.GameId, "bots-" + run.Id, run.Script.LiveKey);
            run.Handle = await streamer.MountAsync(cancellationToken: run.Cancellation.Token);

            run.Http = new HttpClient(new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (_, certificate, _, _) =>
                {
                    if (certificate is null || run.Root is null) return false;
                    using var candidate = new X509Certificate2(certificate);
                    using var validator = new X509Chain();
                    validator.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                    validator.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                    validator.ChainPolicy.CustomTrustStore.Add(run.Root);
                    if (run.NodeAuthority is { } nodeAuthority)
                        validator.ChainPolicy.ExtraStore.Add(nodeAuthority);
                    return validator.Build(candidate);
                }
            });
            var poster = new AudienceEventPoster(run.Http, run.Config.IngressBaseUrl,
                run.Handle.LiveKey, run.Id + "-", () => Interlocked.Increment(ref _sequence));
            run.Poster = poster;
            var runner = new BotFleetRunner(run.Script, poster);
            run.Runner = runner;
            run.Begin();
            await runner.RunAsync(run.Cancellation.Token);
            run.Hold("Completed", runner.Snapshot(), null, []);
            if (run.Config.ReconcileRoomId is { } room)
                await ReconcileAsync(run, room);
        }
        catch (OperationCanceledException) when (run.Cancellation.IsCancellationRequested)
        {
            run.Hold("Stopped", run.Runner?.Snapshot(), null, []);
        }
        catch (Grpc.Core.RpcException ex) when (run.Cancellation.IsCancellationRequested
            && ex.StatusCode == Grpc.Core.StatusCode.Cancelled)
        {
            // A gRPC call the operator stopped answers as a cancelled call, not a bare
            // OperationCanceledException — it is still the operator's stop.
            run.Hold("Stopped", run.Runner?.Snapshot(), null, []);
        }
        catch (Exception ex)
        {
            run.Hold("Failed", run.Runner?.Snapshot(), ex.Message, []);
        }
        finally
        {
            run.Channel?.Dispose();
            run.Http?.Dispose();
            run.ClientCertificate?.Dispose();
            run.Root?.Dispose();
            run.NodeAuthority?.Dispose();
        }
    }

    /// <summary>Holds a finished run to its word against the data plane: polls the room's
    /// ledger until the result freezes (or the budget runs out), then checks deliveries
    /// always and scores/grants when the faction is named. Deviations flip the state.</summary>
    private async Task ReconcileAsync(RunState run, string roomId)
    {
        var connection = run.Config.ReconcileConnectionString;
        if (connection is null)
        {
            run.Hold("Deviated", run.Runner?.Snapshot(), null,
            [
                "reconciliation: room " + roomId + " named but no data-plane connection configured"
            ]);
            return;
        }

        RoomLedger? ledger = null;
        var deadline = DateTimeOffset.UtcNow + ReconcileBudget;
        while (DateTimeOffset.UtcNow < deadline)
        {
            ledger = await BotReconciliation.ReadRoomAsync(connection, roomId);
            if (ledger.ResultScores.Count > 0) break;
            await Task.Delay(TimeSpan.FromSeconds(1), run.Cancellation.Token);
        }
        ledger ??= await BotReconciliation.ReadRoomAsync(connection, roomId);
        var poster = run.Poster!.Snapshot();
        IReadOnlyList<string> deviations;
        if (run.Config.ReconcileFaction is { } faction)
            deviations = BotReconciliation.Deviations(
                BotReconciliation.Expect([(run.Handle!, faction, run.Script)]),
                ledger, [poster]).Deviations;
        else
            deviations = BotReconciliation.DeliveryDeviations(run.Script.Totals.ByKind, ledger, [poster]);
        run.Hold(deviations.Count > 0 ? "Deviated" : "Reconciled",
            run.Runner?.Snapshot(), null, deviations);
    }

    /// <summary>Keeps the registry bounded: past the cap, the oldest terminal runs fall
    /// off the bottom. Live runs never leave — a run one cannot watch or stop is worse
    /// than a fat panel.</summary>
    private void TrimLocked()
    {
        if (_runs.Count <= KeepRuns) return;
        foreach (var id in _runs.Values.Where(r => Terminal(r.State)).OrderBy(r => r.Number)
                     .Take(_runs.Count - KeepRuns).Select(r => r.Id).ToList())
            _runs.Remove(id);
    }
}

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Chorus.Rpc;
using Grpc.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Chorus.NodeAgent;

/// <summary>
/// The node agent (architecture 10.1-10.2): pulls the signed bootstrap seeds, supervises them,
/// reports processes to the control plane and applies deployment plans idempotently. Commands
/// are journaled before they execute, a re-delivered CommandId never pulls a second process,
/// and a process is only signalled after its PID and start time match the journal — a reused
/// PID is never killed blindly.
/// </summary>
public sealed class BootstrapAgent(ILogger<BootstrapAgent> logger) : BackgroundService
{
    private sealed record JournalEntry(string SlotId, string ProcessInstanceId, int Pid, DateTime StartedAtUtc,
        string DesiredState, string CommandId, DateTime? QuarantinedAtUtc = null);
    private sealed record ManagedSlot(BootstrapSeedSlot Spec, string InstanceId, Process Process,
        DateTime StartedAtUtc, WindowsJob? Job);

    /// <summary>Agent-level variables the seed hosts inherit — the lab topology the agent
    /// itself runs under (coordinator endpoints, tenant applications, optional lab platform
    /// credentials), never node-authority secrets.</summary>
    private static readonly string[] PassthroughVariables =
    [
        "CHORUS_GAME_ENDPOINTS", "CHORUS_MATCH_INTERVAL_MS", "CHORUS_DATA_ENDPOINT",
        "CHORUS_TENANT_APPLICATIONS", "CHORUS_ACCESS_CREDENTIAL_KEY",
        "CHORUS_PLATFORM_SIMULATOR", "CHORUS_SIMULATOR_SIGNING_KEY",
        "CHORUS_DEV_PLATFORM_ENDPOINT", "CHORUS_WSS_PORT", "CHORUS_INGRESS_PORT"
    ];

    private readonly object _gate = new();
    private readonly Dictionary<string, ManagedSlot> _slots = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _desiredState = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _lastCommandBySlot = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTime> _drainRequestedUtc = new(StringComparer.Ordinal);
    private int _drainForceSeconds = 300;
    // FIX-AGENT-03: crash restart policy (architecture 10.2) — exponential backoff, and a
    // slot that keeps crashing inside the window is quarantined instead of restarted.
    private static readonly int[] CrashBackoffSeconds = [1, 2, 4, 8, 16, 30];
    private readonly Dictionary<string, List<DateTime>> _restartTimesUtc = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTime> _nextRestartUtc = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTime> _quarantinedUtc = new(StringComparer.Ordinal);
    private int _crashWindowSeconds = 600;
    private BootstrapManifestPayload _manifest = null!;
    private string _stateDirectory = null!;
    private string _journalPath = null!;
    private X509Certificate2 _root = null!;
    private X509Certificate2 _nodeAuthority = null!;
    private X509Certificate2 _agentCertificate = null!;
    private string _agentInstanceId = Guid.NewGuid().ToString("N");
    private long _reportSequence;
    // FIX-OBS-06: the machine telemetry leg — the sampler feeds the pipeline, the loop drains
    // it on its own period, and the flush runs on exit. The sampler/pipeline are the
    // testable core; this agent only owns the identity, the period and the transport.
    private MachineTelemetrySampler _telemetrySampler = null!;
    private MachineTelemetryPipeline _telemetryPipeline = null!;
    private TimeSpan _telemetryPeriod = TimeSpan.FromSeconds(15);
    private DateTime _telemetryNextUtc = DateTime.UtcNow;
    private long _telemetrySequence;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Initialize();
        ReclaimPreviousInstances();
        try
        {
            var plans = WatchPlansAsync(stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                lock (_gate)
                {
                    var nowUtc = DateTime.UtcNow;
                    foreach (var spec in _manifest.SeedSlots)
                    {
                        // A plan can park a slot down (Draining/Stopped): the supervisor does
                        // not restart what the control plane asked to stop — and Draining is
                        // not a stop at all (FIX-AGENT-02): the parked slot's process stays up.
                        if (_desiredState.GetValueOrDefault(spec.SlotId, "Running") != "Running") continue;
                        // FIX-AGENT-03: a quarantined slot is never restarted by the supervisor —
                        // returning it to service is the operator's fresh Running plan.
                        if (_quarantinedUtc.ContainsKey(spec.SlotId)) continue;
                        if (!_slots.TryGetValue(spec.SlotId, out var running))
                        {
                            // A slot the agent has never started starts immediately: a fresh
                            // bootstrap (or a restarted agent reclaiming its journal) is not a
                            // crash — it must not wait out a backoff or spend restart budget.
                            _slots[spec.SlotId] = StartSlot(spec);
                            SaveJournal();
                            continue;
                        }
                        if (!running.Process.HasExited) continue;
                        // FIX-AGENT-03 (architecture 10.2): an exited slot under a Running
                        // desired state crashed. Restart on an exponential backoff
                        // (1/2/4/8/16/30s); more than five restarts inside the crash window
                        // quarantine the slot instead — the process keeps dying, the node
                        // keeps its resources, and the state stays visible in the report.
                        var restarts = _restartTimesUtc.TryGetValue(spec.SlotId, out var times)
                            ? times : [];
                        restarts.RemoveAll(at => nowUtc - at > TimeSpan.FromSeconds(_crashWindowSeconds));
                        if (restarts.Count >= 5)
                        {
                            _quarantinedUtc[spec.SlotId] = nowUtc;
                            _nextRestartUtc.Remove(spec.SlotId);
                            logger.LogWarning(
                                "Slot {SlotId} crashed {Restarts} times within {Seconds}s; quarantined until a fresh plan",
                                spec.SlotId, restarts.Count, _crashWindowSeconds);
                            SaveJournal();
                            continue;
                        }
                        if (!_nextRestartUtc.TryGetValue(spec.SlotId, out var due))
                            _nextRestartUtc[spec.SlotId] = due = nowUtc.AddSeconds(
                                CrashBackoffSeconds[Math.Min(restarts.Count, CrashBackoffSeconds.Length - 1)]);
                        if (nowUtc < due) continue;
                        _nextRestartUtc.Remove(spec.SlotId);
                        running?.Job?.Dispose();
                        running?.Process.Dispose();
                        restarts.Add(nowUtc);
                        _restartTimesUtc[spec.SlotId] = restarts;
                        logger.LogWarning(
                            "Slot {SlotId} exited; restart {Restarts} of {Window}s window due in {Backoff}s",
                            spec.SlotId, restarts.Count, _crashWindowSeconds,
                            CrashBackoffSeconds[Math.Min(restarts.Count - 1, CrashBackoffSeconds.Length - 1)]);
                        _slots[spec.SlotId] = StartSlot(spec);
                        SaveJournal();
                    }
                    // FIX-AGENT-02: the drain wait is bounded. A Draining slot whose process
                    // outlives the force deadline (CHORUS_DRAIN_FORCE_SECONDS, default 300s)
                    // is stopped locally so the node converges even if the control plane's
                    // business-drain confirmation never arrives.
                    foreach (var (slotId, requestedUtc) in _drainRequestedUtc)
                    {
                        if (_desiredState.GetValueOrDefault(slotId) != "Draining") continue;
                        if (!_slots.TryGetValue(slotId, out var draining) || draining.Process.HasExited) continue;
                        if (nowUtc - requestedUtc <= TimeSpan.FromSeconds(_drainForceSeconds)) continue;
                        logger.LogWarning(
                            "Slot {SlotId} outlived its drain deadline ({Seconds}s); forcing the stop",
                            slotId, _drainForceSeconds);
                        StopSlotGracefully(draining);
                    }
                }
                await ReportAsync(stoppingToken);
                await ReportTelemetryAsync(stoppingToken);
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
            await plans;
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            lock (_gate)
            {
                foreach (var slot in _slots.Values) StopSlot(slot);
                _slots.Clear();
                SaveJournal();
            }
            // FIX-OBS-06: give the buffered telemetry its last chance to land before exit.
            // The flush is bounded — a dead control plane must not hold the shutdown.
            if (_telemetryPipeline is not null)
            {
                using var flushCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try { await _telemetryPipeline.FlushAsync(SendTelemetryAsync, flushCts.Token); }
                catch (OperationCanceledException) { }
            }
            _agentCertificate.Dispose();
            _nodeAuthority.Dispose();
            _root.Dispose();
        }
    }

    private void Initialize()
    {
        var manifestPath = Required("CHORUS_BOOTSTRAP_MANIFEST");
        var manifestDirectory = Path.GetDirectoryName(Path.GetFullPath(manifestPath))!;
        var signed = JsonSerializer.Deserialize<SignedBootstrapManifest>(File.ReadAllText(manifestPath))
            ?? throw new InvalidOperationException("Bootstrap manifest is unreadable.");
        var rootPath = Path.GetFullPath(Required("CHORUS_ROOT_CA_PEM"));
        _root = X509Certificate2.CreateFromPem(File.ReadAllText(rootPath));
        _manifest = BootstrapManifest.Verify(signed, _root);
        if (Path.GetFullPath(Path.Combine(manifestDirectory, _manifest.RootCaPemPath)) != rootPath)
            throw new InvalidOperationException("Bootstrap root path differs from the signed manifest.");
        var nodePfxPath = Path.GetFullPath(Path.Combine(manifestDirectory, _manifest.NodeAuthorityPfxPath));
        _nodeAuthority = X509CertificateLoader.LoadPkcs12FromFile(nodePfxPath,
            Required("CHORUS_NODE_AUTHORITY_PFX_PASSWORD"), X509KeyStorageFlags.EphemeralKeySet);
        var agentIdentity = new ProcessIdentity(_manifest.ClusterId, "@cluster", _manifest.NodeId,
            "node-agent", _agentInstanceId);
        _agentCertificate = ProcessCertificateIssuer.CreateProcess(_nodeAuthority, agentIdentity,
            DateTimeOffset.UtcNow.AddHours(24));
        if (ProcessCertificateValidator.Validate(_agentCertificate, _root, _manifest.ClusterId, [_nodeAuthority]) is null)
            throw new InvalidOperationException("Node authority is not authorized by the cluster root.");
        _stateDirectory = Path.GetFullPath(Required("CHORUS_AGENT_STATE_DIR"));
        Directory.CreateDirectory(_stateDirectory);
        if (Environment.GetEnvironmentVariable("CHORUS_DRAIN_FORCE_SECONDS") is { Length: > 0 } forceText
            && int.TryParse(forceText, out var forceSeconds) && forceSeconds > 0)
            _drainForceSeconds = forceSeconds;
        if (Environment.GetEnvironmentVariable("CHORUS_CRASH_WINDOW_SECONDS") is { Length: > 0 } windowText
            && int.TryParse(windowText, out var windowSeconds) && windowSeconds > 0)
            _crashWindowSeconds = windowSeconds;
        // FIX-OBS-06: the telemetry leg's tuning. The sampling period and the reliability
        // knobs (buffer cap, retry budget, backoff) are all configurable; defaults keep a
        // 15s cadence with a bounded buffer and retry so a long control-plane outage drops
        // stale samples instead of accumulating a backlog.
        if (Environment.GetEnvironmentVariable("CHORUS_TELEMETRY_PERIOD_SECONDS") is { Length: > 0 } periodText
            && int.TryParse(periodText, out var telemetrySeconds) && telemetrySeconds > 0)
            _telemetryPeriod = TimeSpan.FromSeconds(telemetrySeconds);
        var maxPending = Environment.GetEnvironmentVariable("CHORUS_TELEMETRY_MAX_PENDING") is { Length: > 0 } pendingText
            && int.TryParse(pendingText, out var pending) && pending > 0 ? pending : 20;
        var maxAttempts = Environment.GetEnvironmentVariable("CHORUS_TELEMETRY_MAX_ATTEMPTS") is { Length: > 0 } attemptsText
            && int.TryParse(attemptsText, out var attempts) && attempts > 0 ? attempts : 5;
        var backoffMs = Environment.GetEnvironmentVariable("CHORUS_TELEMETRY_RETRY_BACKOFF_MS") is { Length: > 0 } backoffText
            && int.TryParse(backoffText, out var backoff) && backoff >= 0 ? backoff : 1000;
        _telemetrySampler = new MachineTelemetrySampler();
        _telemetryPipeline = new MachineTelemetryPipeline(maxPending, maxAttempts,
            TimeSpan.FromMilliseconds(backoffMs), logger);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(_stateDirectory,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        _journalPath = Path.Combine(_stateDirectory, "process-journal.json");
        LoadJournalState();
    }

    /// <summary>Restores the persisted desired states and applied commands — a restarted agent
    /// keeps the plans it already received and does not undo them (architecture 10.1).</summary>
    private void LoadJournalState()
    {
        if (!File.Exists(_journalPath)) return;
        var entries = JsonSerializer.Deserialize<JournalEntry[]>(File.ReadAllText(_journalPath)) ?? [];
        foreach (var entry in entries)
        {
            _desiredState[entry.SlotId] = entry.DesiredState;
            if (entry.CommandId.Length > 0) _lastCommandBySlot[entry.SlotId] = entry.CommandId;
            if (entry.QuarantinedAtUtc is { } quarantinedUtc) _quarantinedUtc[entry.SlotId] = quarantinedUtc;
        }
    }

    private ManagedSlot StartSlot(BootstrapSeedSlot spec)
    {
        var assembly = Path.GetFullPath(spec.AssemblyPath);
        if (!File.Exists(assembly) || !StringComparer.Ordinal.Equals(
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly))), spec.Sha256Hex))
            throw new InvalidOperationException($"Bootstrap package hash differs for {spec.SlotId}.");
        var instanceId = Guid.NewGuid().ToString("N");
        using var certificate = ProcessCertificateIssuer.CreateProcess(_nodeAuthority,
            new ProcessIdentity(_manifest.ClusterId, "@cluster", _manifest.NodeId, spec.Role, instanceId),
            DateTimeOffset.UtcNow.AddHours(24), "localhost");
        var certDirectory = Path.Combine(_stateDirectory, instanceId);
        Directory.CreateDirectory(certDirectory);
        var certificatePath = Path.Combine(certDirectory, "process.pem");
        var keyPath = Path.Combine(certDirectory, "process.key");
        File.WriteAllText(certificatePath, certificate.ExportCertificatePem());
        using (var key = certificate.GetECDsaPrivateKey())
            File.WriteAllText(keyPath, key!.ExportPkcs8PrivateKeyPem());
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(certDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.SetUnixFileMode(certificatePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.SetUnixFileMode(keyPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        var start = new ProcessStartInfo();
        if (OperatingSystem.IsLinux())
        {
            start.FileName = "/usr/bin/setsid";
            start.ArgumentList.Add("dotnet");
            start.ArgumentList.Add(assembly);
        }
        else
        {
            start.FileName = "dotnet";
            start.ArgumentList.Add(assembly);
        }
        start.UseShellExecute = false;
        start.Environment.Remove("CHORUS_NODE_AUTHORITY_PFX_PASSWORD");
        start.Environment.Remove("CHORUS_BOOTSTRAP_MANIFEST");
        start.Environment.Remove("CHORUS_ROOT_PFX_PASSWORD");
        foreach (var name in PassthroughVariables)
            if (Environment.GetEnvironmentVariable(name) is { Length: > 0 } value)
                start.Environment[name] = value;
        start.Environment["CHORUS_DATABASE_CONNECTION_STRING"] = Required("CHORUS_DATABASE_CONNECTION_STRING");
        start.Environment["CHORUS_CLUSTER_ID"] = _manifest.ClusterId;
        start.Environment["CHORUS_ROOT_CA_PEM"] = Path.GetFullPath(Required("CHORUS_ROOT_CA_PEM"));
        start.Environment["CHORUS_NODE_CA_PEM"] = Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(Path.GetFullPath(Required("CHORUS_BOOTSTRAP_MANIFEST")))!, _manifest.NodeCaPemPath));
        start.Environment["CHORUS_TLS_CERT_PEM"] = certificatePath;
        start.Environment["CHORUS_TLS_KEY_PEM"] = keyPath;
        start.Environment["CHORUS_PORT"] = spec.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        // The browser face (FIX-WEB-03) gives every control server a second, operator-facing
        // listener; the manifest pins only the cluster ports, so each seed takes a free
        // ephemeral port here — two supervised control servers would otherwise fight over
        // the fixed default and one would die with an address-in-use at startup.
        if (spec.Role == "control-server")
            start.Environment["CHORUS_BROWSER_PORT"] = FreePort().ToString(
                System.Globalization.CultureInfo.InvariantCulture);
        // Every supervised service that speaks to the data plane gets the node's seeded data
        // port (FIX-AGENT-01); the streamer gateway additionally routes to the seeded streamer
        // port and the control endpoint the manifest pins.
        if (_manifest.SeedSlots.FirstOrDefault(slot => slot.Role == "data-server") is { } dataSlot)
            start.Environment["CHORUS_DATA_PORT"] = dataSlot.Port.ToString(
                System.Globalization.CultureInfo.InvariantCulture);
        if (spec.Role is "streamer-gateway" or "platform-gateway")
        {
            if (spec.Role == "streamer-gateway" &&
                _manifest.SeedSlots.FirstOrDefault(slot => slot.Role == "streamer-server") is { } streamerSlot)
                start.Environment["CHORUS_STREAMER_PORT"] = streamerSlot.Port.ToString(
                    System.Globalization.CultureInfo.InvariantCulture);
            // Both gateways self-report to the control plane through the endpoint the
            // manifest pins; the operator tasks they reconcile also live there.
            start.Environment["CHORUS_CONTROL_ENDPOINT"] = _manifest.ControlEndpoint;
        }
        if (spec.Role == "game-server" &&
            Environment.GetEnvironmentVariable("CHORUS_GAME_CLIENT_TENANT") is { Length: > 0 } tenant)
        {
            // For a tenant the game host serves, the agent mints its tenant-scoped client
            // identity from the same grant.
            using var clientCertificate = ProcessCertificateIssuer.CreateProcess(_nodeAuthority,
                new ProcessIdentity(_manifest.ClusterId, tenant, _manifest.NodeId, spec.Role,
                    instanceId + "-client"),
                DateTimeOffset.UtcNow.AddHours(24), "localhost");
            var clientCertificatePath = Path.Combine(certDirectory, "process-client.pem");
            var clientKeyPath = Path.Combine(certDirectory, "process-client.key");
            File.WriteAllText(clientCertificatePath, clientCertificate.ExportCertificatePem());
            using (var clientKey = clientCertificate.GetECDsaPrivateKey())
                File.WriteAllText(clientKeyPath, clientKey!.ExportPkcs8PrivateKeyPem());
            start.Environment["CHORUS_CLIENT_CERT_PEM"] = clientCertificatePath;
            start.Environment["CHORUS_CLIENT_KEY_PEM"] = clientKeyPath;
        }
        var process = Process.Start(start) ?? throw new InvalidOperationException($"Could not start {spec.SlotId}.");
        var job = OperatingSystem.IsWindows() ? WindowsJob.Attach(process) : null;
        logger.LogInformation("Started bootstrap slot {SlotId} as instance {InstanceId}", spec.SlotId, instanceId);
        return new ManagedSlot(spec, instanceId, process, process.StartTime.ToUniversalTime(), job);
    }

    private async Task ReportAsync(CancellationToken token)
    {
        try
        {
            using var channel = ProcessGrpcClientFactory.Create(new Uri(_manifest.ControlEndpoint),
                _agentCertificate, _root, _nodeAuthority, _manifest.ClusterId, "control-server");
            var control = new ControlService.ControlServiceClient(channel);
            await control.RegisterNodeAsync(new RegisterNodeRequest
            {
                NodeId = _manifest.NodeId, AgentInstanceId = _agentInstanceId,
                CapabilitiesJson = JsonSerializer.Serialize(new
                {
                    processTree = OperatingSystem.IsWindows() ? "windows-job-object" : "linux-process-tree",
                    resourceLimits = OperatingSystem.IsLinux() && File.Exists("/sys/fs/cgroup/cgroup.controllers")
                        ? "cgroup-v2-detected-not-enforced" : "not-enforced"
                })
            }, cancellationToken: token);
            List<ManagedSlot> snapshot;
            lock (_gate) snapshot = [.. _slots.Values];
            foreach (var slot in snapshot)
            {
                var ready = false;
                // The agent knows the node-local serving address: the manifest host with the
                // slot's port — exactly what the routing snapshot needs to reach the process.
                var endpoint = new UriBuilder(_manifest.ControlEndpoint) { Port = slot.Spec.Port }.Uri;
                try
                {
                    using var roleChannel = ProcessGrpcClientFactory.Create(endpoint,
                        _agentCertificate, _root, _nodeAuthority, _manifest.ClusterId, slot.Spec.Role);
                    if (slot.Spec.Role == "control-server")
                        ready = (await new ControlService.ControlServiceClient(roleChannel)
                            .ReadyAsync(new Empty(), cancellationToken: token)).Ready;
                    else if (slot.Spec.Role == "game-server")
                        ready = (await new GameHost.GameHostClient(roleChannel)
                            .ReadyAsync(new Empty(), cancellationToken: token)).Ready;
                    else if (slot.Spec.Role == "streamer-server")
                        ready = (await new StreamerSession.StreamerSessionClient(roleChannel)
                            .ReadyAsync(new Empty(), cancellationToken: token)).Ready;
                    else if (slot.Spec.Role == "streamer-gateway")
                        ready = (await new StreamerGatewayFace.StreamerGatewayFaceClient(roleChannel)
                            .ReadyAsync(new Empty(), cancellationToken: token)).Ready;
                    else if (slot.Spec.Role == "platform-gateway")
                        ready = (await new PlatformGatewayFace.PlatformGatewayFaceClient(roleChannel)
                            .ReadyAsync(new Empty(), cancellationToken: token)).Ready;
                    else
                        ready = (await new DataService.DataServiceClient(roleChannel)
                            .ReadyAsync(new Empty(), cancellationToken: token)).Ready;
                }
                catch (Exception error) when (error is RpcException or HttpRequestException)
                {
                    logger.LogWarning("Bootstrap slot {SlotId} is not ready", slot.Spec.SlotId);
                }
                await control.ReportProcessAsync(new ReportProcessRequest
                {
                    NodeId = _manifest.NodeId, AgentInstanceId = _agentInstanceId,
                    SlotId = slot.Spec.SlotId, Role = slot.Spec.Role, ProcessInstanceId = slot.InstanceId,
                    ReportSequence = Interlocked.Increment(ref _reportSequence),
                    Lifecycle = slot.Process.HasExited ? "Exited" : "Running",
                    Readiness = ready ? "Ready" : "NotReady",
                    // FIX-AGENT-03: a quarantined slot reports the isolation in its health so
                    // the control plane sees why the node stopped serving the slot.
                    Health = _quarantinedUtc.ContainsKey(slot.Spec.SlotId) ? "Quarantined"
                        : ready ? "Healthy" : "Degraded",
                    // FIX-AGENT-02: a parked Draining slot stays alive but reports Draining —
                    // that is the admission the scheduler filters new assignments by.
                    Admission = _desiredState.GetValueOrDefault(slot.Spec.SlotId, "Running") == "Draining"
                        ? "Draining" : "Accepting",
                    EntityRecovery = "NotStarted",
                    StartedUnixMs = new DateTimeOffset(slot.StartedAtUtc).ToUnixTimeMilliseconds(),
                    Endpoint = endpoint.ToString()
                }, cancellationToken: token);
            }
        }
        catch (Exception error) when (error is RpcException or HttpRequestException)
        {
            // The error detail is the only window into why: a refused dial, a refused plan
            // stream, or a server-side register rejection all land on this one warning.
            logger.LogWarning(error, "ControlServer is unavailable; signed bootstrap slots remain supervised locally");
        }
    }

    /// <summary>
    /// FIX-OBS-06: samples the machine collectors on the telemetry period, buffers the
    /// snapshot, then drains the buffer to the control plane. The drain stops at the first
    /// give-up — an outage does not burn its retry budget on every buffered sample, and a
    /// recovery drains the backlog quickly (each sample sends on its first attempt).
    /// </summary>
    private async Task ReportTelemetryAsync(CancellationToken token)
    {
        var nowUtc = DateTime.UtcNow;
        if (nowUtc < _telemetryNextUtc) return;
        _telemetryNextUtc = nowUtc + _telemetryPeriod;
        _telemetryPipeline.Enqueue(_telemetrySampler.Sample(
            new DateTimeOffset(nowUtc).ToUnixTimeMilliseconds()));
        while (_telemetryPipeline.PendingCount > 0)
        {
            if (!await _telemetryPipeline.SendOldestAsync(SendTelemetryAsync, token)) break;
        }
    }

    /// <summary>FIX-OBS-06: sends one telemetry snapshot to the control plane over the
    /// agent's mTLS channel, mirroring the process-report transport. Transport failures
    /// (refused dial, unavailable) surface as RpcException/HttpRequestException so the
    /// pipeline's bounded retry can hold or drop the sample.</summary>
    private async Task SendTelemetryAsync(MachineTelemetrySample sample, CancellationToken token)
    {
        using var channel = ProcessGrpcClientFactory.Create(new Uri(_manifest.ControlEndpoint),
            _agentCertificate, _root, _nodeAuthority, _manifest.ClusterId, "control-server");
        var control = new ControlService.ControlServiceClient(channel);
        var request = MachineTelemetryMapper.ToRequest(sample, _manifest.NodeId, _agentInstanceId,
            Interlocked.Increment(ref _telemetrySequence));
        await control.ReportMachineTelemetryAsync(request, cancellationToken: token);
    }

    /// <summary>
    /// The agent's plan stream: reconnects forever, and applies every arriving plan
    /// idempotently. An outage ends the stream; the control plane re-emits the full plan set
    /// on reconnect and the deduplicated commands must not disturb running processes.
    /// </summary>
    private async Task WatchPlansAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var channel = ProcessGrpcClientFactory.Create(new Uri(_manifest.ControlEndpoint),
                    _agentCertificate, _root, _nodeAuthority, _manifest.ClusterId, "control-server");
                var deployment = new Deployment.DeploymentClient(channel);
                using var call = deployment.WatchPlans(
                    new WatchPlansRequest { NodeId = _manifest.NodeId }, cancellationToken: stoppingToken);
                while (await call.ResponseStream.MoveNext(stoppingToken))
                    ApplyPlanLocally(call.ResponseStream.Current);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception error) when (error is RpcException or HttpRequestException)
            {
                logger.LogWarning(error, "WatchPlans broke; retrying");
                try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            }
        }
    }

    private void ApplyPlanLocally(DeploymentPlanProto plan)
    {
        lock (_gate)
        {
            var spec = _manifest.SeedSlots.FirstOrDefault(slot => slot.SlotId == plan.SlotId);
            if (spec is null)
            {
                // The node holds only its bootstrap packages (architecture 10.1): a plan for an
                // unknown slot has nothing to run on — refuse, do not improvise.
                logger.LogWarning("Plan {SlotId} names a slot this node does not bootstrap; ignored", plan.SlotId);
                return;
            }
            if (plan.CommandExpiresUnixMs > 0 && DateTimeOffset.FromUnixTimeMilliseconds(plan.CommandExpiresUnixMs)
                .UtcDateTime <= DateTimeOffset.UtcNow)
            {
                logger.LogWarning("Plan {SlotId} command {CommandId} arrived after its expiry; ignored",
                    plan.SlotId, plan.CommandId);
                return;
            }
            if (plan.CommandId.Length > 0 && _lastCommandBySlot.TryGetValue(plan.SlotId, out var applied)
                && applied == plan.CommandId)
                return; // a re-delivered command never acts twice
            // Journal first: the command is durable before it executes (architecture 10.2).
            if (plan.CommandId.Length > 0) _lastCommandBySlot[plan.SlotId] = plan.CommandId;
            _desiredState[plan.SlotId] = plan.DesiredState is "Running" or "Draining" or "Drained" or "Stopped"
                ? plan.DesiredState : "Running";
            SaveJournal();
            if (plan.CommandId.Length == 0)
            {
                // A commandless row is a state declaration, not a command (architecture 10.2):
                // bootstrap seed values replay on every WatchPlans reconnect, and re-running
                // them must never start, stop, or reset the slot's crash bookkeeping — the
                // supervisor owns the process lifecycle between real commands.
                logger.LogInformation("Applied plan {SlotId} state {State} (commandless declaration)",
                    plan.SlotId, plan.DesiredState);
                return;
            }
            switch (_desiredState[plan.SlotId])
            {
                case "Running":
                    // A fresh Running command is also the way out of quarantine (FIX-AGENT-03):
                    // like drain, returning a slot to service is an operator act — the plan
                    // clears the quarantine and the restart budget and pulls the slot up.
                    _quarantinedUtc.Remove(plan.SlotId);
                    _restartTimesUtc.Remove(plan.SlotId);
                    _nextRestartUtc.Remove(plan.SlotId);
                    if (!_slots.TryGetValue(plan.SlotId, out var running) || running.Process.HasExited)
                    {
                        if (_slots.TryGetValue(plan.SlotId, out var previous))
                        {
                            previous.Job?.Dispose();
                            previous.Process.Dispose();
                        }
                        _slots[plan.SlotId] = StartSlot(spec);
                        SaveJournal();
                    }
                    break;
                case "Draining":
                    // FIX-AGENT-02: Draining parks the slot. The process keeps serving its
                    // business load (existing rooms finish; the scheduler stops new
                    // assignments because the reported admission is Draining) while the
                    // control plane waits for the drain to complete and escalates the slot
                    // to Drained. The agent only cuts the process loose when the drain
                    // outlives its force deadline (supervisor loop).
                    _drainRequestedUtc[plan.SlotId] = DateTime.UtcNow;
                    break;
                case "Drained":
                case "Stopped":
                    if (_slots.TryGetValue(plan.SlotId, out var stopping)) StopSlotGracefully(stopping);
                    break;
            }
            logger.LogInformation("Applied plan {SlotId} state {State} command {CommandId} generation {Generation}",
                plan.SlotId, plan.DesiredState, plan.CommandId, plan.PlanGeneration);
        }
    }

    /// <summary>Graceful stop: signal only after the PID and start time match the journal, ask
    /// politely first (SIGTERM), then enforce. The stopped slot keeps its journal row with the
    /// desired state so a restarted agent does not lift the stop on its own.</summary>
    private void StopSlotGracefully(ManagedSlot slot)
    {
        try
        {
            if (!slot.Process.HasExited &&
                Math.Abs((slot.Process.StartTime.ToUniversalTime() - slot.StartedAtUtc).TotalSeconds) < 1)
            {
                if (OperatingSystem.IsLinux()) _ = kill(slot.Process.Id, 15);
                else slot.Process.Kill(entireProcessTree: false);
                slot.Process.WaitForExit(TimeSpan.FromSeconds(10));
                if (!slot.Process.HasExited) slot.Process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException) { }
        catch (ArgumentException) { }
        slot.Job?.Dispose();
        slot.Process.Dispose();
        _slots.Remove(slot.Spec.SlotId);
        // Keep the slot's journal entry parked (Pid 0) so the desired state survives.
        SaveJournal();
    }

    private void ReclaimPreviousInstances()
    {
        if (!File.Exists(_journalPath)) return;
        var previous = JsonSerializer.Deserialize<JournalEntry[]>(File.ReadAllText(_journalPath)) ?? [];
        foreach (var entry in previous)
        {
            if (entry.Pid <= 0) continue;
            try
            {
                using var process = Process.GetProcessById(entry.Pid);
                if (Math.Abs((process.StartTime.ToUniversalTime() - entry.StartedAtUtc).TotalSeconds) < 1)
                    process.Kill(entireProcessTree: true);
            }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
        }
    }

    private void SaveJournal()
    {
        var entries = _manifest.SeedSlots.Select(spec =>
        {
            // The quarantine marker rides the journal (FIX-AGENT-03): a restarted agent
            // keeps the isolation instead of silently resuming the restart storm.
            DateTime? quarantinedUtc =
                _quarantinedUtc.TryGetValue(spec.SlotId, out var quarantined) ? quarantined : null;
            return _slots.TryGetValue(spec.SlotId, out var slot)
                ? new JournalEntry(spec.SlotId, slot.InstanceId, slot.Process.HasExited ? 0 : slot.Process.Id,
                    slot.StartedAtUtc, _desiredState.GetValueOrDefault(spec.SlotId, "Running"),
                    _lastCommandBySlot.GetValueOrDefault(spec.SlotId, ""), quarantinedUtc)
                // A stopped slot keeps its row parked so the desired state and command id persist.
                : new JournalEntry(spec.SlotId, "", 0, DateTime.MinValue,
                    _desiredState.GetValueOrDefault(spec.SlotId, "Running"),
                    _lastCommandBySlot.GetValueOrDefault(spec.SlotId, ""), quarantinedUtc);
        }).ToArray();
        var pending = _journalPath + ".pending";
        File.WriteAllText(pending, JsonSerializer.Serialize(entries));
        File.Move(pending, _journalPath, overwrite: true);
    }

    private static void StopSlot(ManagedSlot slot)
    {
        if (!slot.Process.HasExited)
        {
            slot.Job?.Terminate();
            if (!slot.Process.HasExited) slot.Process.Kill(entireProcessTree: true);
        }
        slot.Job?.Dispose();
        slot.Process.Dispose();
    }

    private static string Required(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
        ? value : throw new InvalidOperationException($"Set {name} before starting NodeAgent.");

    private static int FreePort()
    {
        using var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        probe.Start();
        return ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int pid, int signal);
}

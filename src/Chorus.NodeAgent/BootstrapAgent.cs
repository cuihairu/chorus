using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Chorus.Redesign.Rpc;
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
        string DesiredState, string CommandId);
    private sealed record ManagedSlot(BootstrapSeedSlot Spec, string InstanceId, Process Process,
        DateTime StartedAtUtc, WindowsJob? Job);

    /// <summary>Agent-level variables the seed hosts inherit — the lab topology the agent
    /// itself runs under (coordinator endpoints), never node-authority secrets.</summary>
    private static readonly string[] PassthroughVariables =
        ["CHORUS_GAME_ENDPOINTS", "CHORUS_MATCH_INTERVAL_MS", "CHORUS_DATA_ENDPOINT"];

    private readonly object _gate = new();
    private readonly Dictionary<string, ManagedSlot> _slots = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _desiredState = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _lastCommandBySlot = new(StringComparer.Ordinal);
    private BootstrapManifestPayload _manifest = null!;
    private string _stateDirectory = null!;
    private string _journalPath = null!;
    private X509Certificate2 _root = null!;
    private X509Certificate2 _nodeAuthority = null!;
    private X509Certificate2 _agentCertificate = null!;
    private string _agentInstanceId = Guid.NewGuid().ToString("N");
    private long _reportSequence;

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
                    foreach (var spec in _manifest.SeedSlots)
                    {
                        // A plan can park a slot down (Draining/Stopped): the supervisor does
                        // not restart what the control plane asked to stop.
                        if (_desiredState.GetValueOrDefault(spec.SlotId, "Running") != "Running") continue;
                        if (!_slots.TryGetValue(spec.SlotId, out var running) || running.Process.HasExited)
                        {
                            running?.Job?.Dispose();
                            running?.Process.Dispose();
                            _slots[spec.SlotId] = StartSlot(spec);
                            SaveJournal();
                        }
                    }
                }
                await ReportAsync(stoppingToken);
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
        if (spec.Role == "game-server")
        {
            // The game host speaks to the data plane this node seeds, and for a tenant it
            // serves: the agent mints its tenant-scoped client identity from the same grant.
            if (_manifest.SeedSlots.FirstOrDefault(slot => slot.Role == "data-server") is { } dataSlot)
                start.Environment["CHORUS_DATA_PORT"] = dataSlot.Port.ToString(
                    System.Globalization.CultureInfo.InvariantCulture);
            if (Environment.GetEnvironmentVariable("CHORUS_GAME_CLIENT_TENANT") is { Length: > 0 } tenant)
            {
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
                    Health = ready ? "Healthy" : "Degraded", Admission = "Accepting",
                    EntityRecovery = "NotStarted",
                    StartedUnixMs = new DateTimeOffset(slot.StartedAtUtc).ToUnixTimeMilliseconds(),
                    Endpoint = endpoint.ToString()
                }, cancellationToken: token);
            }
        }
        catch (Exception error) when (error is RpcException or HttpRequestException)
        {
            logger.LogWarning("ControlServer is unavailable; signed bootstrap slots remain supervised locally");
        }
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
            switch (_desiredState[plan.SlotId])
            {
                case "Running":
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
        var entries = _manifest.SeedSlots.Select(spec => _slots.TryGetValue(spec.SlotId, out var slot)
                ? new JournalEntry(spec.SlotId, slot.InstanceId, slot.Process.HasExited ? 0 : slot.Process.Id,
                    slot.StartedAtUtc, _desiredState.GetValueOrDefault(spec.SlotId, "Running"),
                    _lastCommandBySlot.GetValueOrDefault(spec.SlotId, ""))
                // A stopped slot keeps its row parked so the desired state and command id persist.
                : new JournalEntry(spec.SlotId, "", 0, DateTime.MinValue,
                    _desiredState.GetValueOrDefault(spec.SlotId, "Running"),
                    _lastCommandBySlot.GetValueOrDefault(spec.SlotId, "")))
            .ToArray();
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

    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int pid, int signal);
}

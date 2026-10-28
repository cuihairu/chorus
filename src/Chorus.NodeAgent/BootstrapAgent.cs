using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Chorus.Redesign.Rpc;
using Grpc.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Chorus.NodeAgent;

public sealed class BootstrapAgent(ILogger<BootstrapAgent> logger) : BackgroundService
{
    private sealed record JournalEntry(string SlotId, string ProcessInstanceId, int Pid, DateTime StartedAtUtc);
    private sealed record ManagedSlot(BootstrapSeedSlot Spec, string InstanceId, Process Process,
        DateTime StartedAtUtc, WindowsJob? Job);

    private readonly Dictionary<string, ManagedSlot> _slots = new(StringComparer.Ordinal);
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
            while (!stoppingToken.IsCancellationRequested)
            {
                foreach (var spec in _manifest.SeedSlots)
                {
                    if (!_slots.TryGetValue(spec.SlotId, out var running) || running.Process.HasExited)
                    {
                        running?.Job?.Dispose();
                        running?.Process.Dispose();
                        _slots[spec.SlotId] = StartSlot(spec);
                        SaveJournal();
                    }
                }

                await ReportAsync(stoppingToken);
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            foreach (var slot in _slots.Values) StopSlot(slot);
            _slots.Clear();
            SaveJournal();
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
        start.Environment["CHORUS_DATABASE_CONNECTION_STRING"] = Required("CHORUS_DATABASE_CONNECTION_STRING");
        start.Environment["CHORUS_CLUSTER_ID"] = _manifest.ClusterId;
        start.Environment["CHORUS_ROOT_CA_PEM"] = Path.GetFullPath(Required("CHORUS_ROOT_CA_PEM"));
        start.Environment["CHORUS_NODE_CA_PEM"] = Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(Path.GetFullPath(Required("CHORUS_BOOTSTRAP_MANIFEST")))!, _manifest.NodeCaPemPath));
        start.Environment["CHORUS_TLS_CERT_PEM"] = certificatePath;
        start.Environment["CHORUS_TLS_KEY_PEM"] = keyPath;
        start.Environment["CHORUS_PORT"] = spec.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
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
            foreach (var slot in _slots.Values)
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

    private void ReclaimPreviousInstances()
    {
        if (!File.Exists(_journalPath)) return;
        var previous = JsonSerializer.Deserialize<JournalEntry[]>(File.ReadAllText(_journalPath)) ?? [];
        foreach (var entry in previous)
        {
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
        var entries = _slots.Values.Select(slot => new JournalEntry(slot.Spec.SlotId,
            slot.InstanceId, slot.Process.Id, slot.StartedAtUtc)).ToArray();
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
}

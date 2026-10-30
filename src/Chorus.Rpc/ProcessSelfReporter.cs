using System.Security.Cryptography.X509Certificates;
using Grpc.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Chorus.Rpc;

/// <summary>One room's progress as its host sees it (FIX-TAKE-06): the actor's lifecycle
/// state and head (FIX-ROOM-01), reported separately from the process readiness — a host
/// whose heartbeats stay healthy may still hold rooms whose actors stopped.</summary>
public sealed record RoomProgressReport(string RoomId, long Epoch, string State, long LastTick,
    long? LastCommittedUnixMs, string? StopReason);

/// <summary>
/// The instance self-report loop (architecture 4.4): a host that is not supervised by a node
/// agent — today the game and streamer hosts — tells the control plane where it serves, so
/// the routing snapshot joins owners and streamer bindings to reachable endpoints. The report
/// speaks with the host's own certificate: the process is its own agent, and the control plane
/// checks the certificate against the claimed instance. A rejected or failed report is retried
/// on the next tick — the node row may not exist yet; reporting only converges, never crashes
/// the host. Each report may carry the host's per-room progress (FIX-TAKE-06).
/// </summary>
public sealed class ProcessSelfReporter(
    X509Certificate2 clientCertificate, X509Certificate2 root, X509Certificate2 nodeAuthority,
    ProcessIdentity identity, string controlEndpoint, string endpoint, string slotId,
    int intervalMs, ILogger<ProcessSelfReporter>? logger = null,
    Func<IReadOnlyList<RoomProgressReport>>? roomProgress = null,
    Func<string>? admission = null) : BackgroundService
{
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var channel = ProcessGrpcClientFactory.Create(new Uri(controlEndpoint),
            clientCertificate, root, nodeAuthority, identity.ClusterId, "control-server");
        var control = new ControlService.ControlServiceClient(channel);
        var startedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var sequence = 1L;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var request = new ReportProcessRequest
                {
                    NodeId = identity.NodeId,
                    AgentInstanceId = identity.ProcessInstanceId,
                    SlotId = slotId,
                    ProcessInstanceId = identity.ProcessInstanceId,
                    ReportSequence = sequence,
                    Lifecycle = "Running",
                    Readiness = "Ready",
                    Health = "Healthy",
                    // The host's own capacity word (FIX-SCHED-02): a GameServer at budget
                    // reports Full so the scheduler stops offering rooms before trying.
                    Admission = admission?.Invoke() ?? ProcessAdmission.Accepting,
                    EntityRecovery = "Steady",
                    StartedUnixMs = startedAtUnixMs,
                    Role = identity.Role,
                    Endpoint = endpoint
                };
                if (roomProgress is not null)
                    foreach (var progress in roomProgress())
                        request.RoomProgress.Add(new RoomProgressProto
                        {
                            RoomId = progress.RoomId, Epoch = progress.Epoch, State = progress.State,
                            LastTick = progress.LastTick,
                            LastCommittedUnixMs = progress.LastCommittedUnixMs ?? 0,
                            StopReason = progress.StopReason ?? ""
                        });
                var reply = await control.ReportProcessAsync(request,
                    deadline: DateTime.UtcNow.Add(CallTimeout), cancellationToken: stoppingToken);
                sequence++;
                if (!reply.Accepted)
                    logger?.LogInformation("Self report was not accepted ({Readiness}); retrying.",
                        reply.Readiness);
            }
            catch (RpcException error) when (error.StatusCode is StatusCode.Unavailable or StatusCode.DeadlineExceeded)
            {
                logger?.LogInformation("Self report to {Endpoint} is not reachable yet.", controlEndpoint);
            }
            try { await Task.Delay(intervalMs, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        }
    }
}

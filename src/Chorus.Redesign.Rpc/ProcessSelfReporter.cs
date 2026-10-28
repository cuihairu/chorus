using System.Security.Cryptography.X509Certificates;
using Grpc.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Chorus.Redesign.Rpc;

/// <summary>
/// The instance self-report loop (architecture 4.4): a host that is not supervised by a node
/// agent — today the game and streamer hosts — tells the control plane where it serves, so
/// the routing snapshot joins owners and streamer bindings to reachable endpoints. The report
/// speaks with the host's own certificate: the process is its own agent, and the control plane
/// checks the certificate against the claimed instance. A rejected or failed report is retried
/// on the next tick — the node row may not exist yet; reporting only converges, never crashes
/// the host.
/// </summary>
public sealed class ProcessSelfReporter(
    X509Certificate2 clientCertificate, X509Certificate2 root, X509Certificate2 nodeAuthority,
    ProcessIdentity identity, string controlEndpoint, string endpoint, string slotId,
    int intervalMs, ILogger<ProcessSelfReporter>? logger = null) : BackgroundService
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
                var reply = await control.ReportProcessAsync(new ReportProcessRequest
                {
                    NodeId = identity.NodeId,
                    AgentInstanceId = identity.ProcessInstanceId,
                    SlotId = slotId,
                    ProcessInstanceId = identity.ProcessInstanceId,
                    ReportSequence = sequence++,
                    Lifecycle = "Running",
                    Readiness = "Ready",
                    Health = "Healthy",
                    Admission = "Open",
                    EntityRecovery = "Steady",
                    StartedUnixMs = startedAtUnixMs,
                    Role = identity.Role,
                    Endpoint = endpoint
                }, deadline: DateTime.UtcNow.Add(CallTimeout), cancellationToken: stoppingToken);
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

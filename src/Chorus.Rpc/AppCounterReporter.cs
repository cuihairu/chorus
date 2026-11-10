using System.Security.Cryptography.X509Certificates;
using Grpc.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Chorus.Rpc;

/// <summary>
/// The app-counter report loop (FIX-OBS-10): a face posts its own cumulative counters to
/// the control plane every interval, speaking with its process certificate the way the
/// process self-report speaks. A failed or unreachable report is retried on the next tick
/// — reporting only converges, never crashes the host. The snapshot is read fresh each
/// tick, so what lands is the value at report time, never a stale one; an empty snapshot
/// sends nothing, since the control plane refuses empty reports.
/// </summary>
public sealed class AppCounterReporter(
    X509Certificate2 clientCertificate, X509Certificate2 root, X509Certificate2 nodeAuthority,
    ProcessIdentity identity, string controlEndpoint, int intervalMs,
    Func<IReadOnlyList<(string Family, long Value)>> snapshot,
    ILogger<AppCounterReporter>? logger = null) : BackgroundService
{
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var channel = ProcessGrpcClientFactory.Create(new Uri(controlEndpoint),
            clientCertificate, root, nodeAuthority, identity.ClusterId, "control-server");
        var control = new ControlService.ControlServiceClient(channel);
        while (!stoppingToken.IsCancellationRequested)
        {
            var samples = snapshot();
            try
            {
                // FIX-OBS-12: an empty counter set is not a report — the control plane
                // refuses empty ones outright, and a face that has observed nothing yet
                // (a gateway with no webhook face and no platform call made) stays silent
                // until it has something to say.
                if (samples.Count > 0)
                {
                    var request = new AppCounterReport
                    {
                        NodeId = identity.NodeId,
                        ProcessInstanceId = identity.ProcessInstanceId,
                        ReportedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                    };
                    foreach (var (family, value) in samples)
                        request.Counters.Add(new AppCounterProto { Family = family, Value = value });
                    var reply = await control.ReportAppCountersAsync(request,
                        deadline: DateTime.UtcNow.Add(CallTimeout), cancellationToken: stoppingToken);
                    if (!reply.Accepted)
                        logger?.LogInformation("App counter report was not accepted; retrying.");
                }
            }
            catch (RpcException error) when (error.StatusCode is StatusCode.Unavailable or StatusCode.DeadlineExceeded)
            {
                logger?.LogInformation("App counter report to {Endpoint} is not reachable yet.", controlEndpoint);
            }
            try { await Task.Delay(intervalMs, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        }
    }
}

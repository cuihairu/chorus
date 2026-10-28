using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Chorus.ControlServer;

/// <summary>
/// The ownership lease registrar (architecture 4.2): renews the lease of every Active
/// ownership while its owner keeps reporting live. A silent owner is not renewed — its lease
/// expires and the entity becomes takeable (10.3). The cadence and windows are the 15s/5s
/// defaults; tests shorten them.
/// </summary>
public sealed class OwnershipRenewalService(RedesignControlStore store,
    ILogger<OwnershipRenewalService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(int.Parse(
        Environment.GetEnvironmentVariable("CHORUS_OWNERSHIP_RENEW_INTERVAL_MS") ?? "5000",
        System.Globalization.CultureInfo.InvariantCulture));
    private static readonly int LeaseSeconds = int.Parse(
        Environment.GetEnvironmentVariable("CHORUS_OWNERSHIP_LEASE_SECONDS") ?? "15",
        System.Globalization.CultureInfo.InvariantCulture);
    private static readonly int GateSeconds = int.Parse(
        Environment.GetEnvironmentVariable("CHORUS_OWNERSHIP_GATE_SECONDS") ?? "15",
        System.Globalization.CultureInfo.InvariantCulture);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var renewed = await store.RenewLiveOwnershipsAsync(LeaseSeconds, GateSeconds, stoppingToken);
                if (renewed > 0)
                    logger.LogDebug("Renewed {Count} live ownership leases", renewed);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error)
            {
                logger.LogWarning(error, "Ownership renewal pass failed");
            }
            try { await Task.Delay(Interval, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}

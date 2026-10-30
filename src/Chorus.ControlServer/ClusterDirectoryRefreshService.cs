namespace Chorus.ControlServer;

/// <summary>
/// The directory refresh loop (FIX-SCHED-01): periodically reads the valid game-host process
/// reports and folds them into the <see cref="ClusterDirectory"/>. New and restarted
/// instances become assignable without a ControlServer restart; an instance whose reports
/// stop arriving ages out of the directory after the staleness window, so its address is
/// never used for a new assignment. A failed pass keeps the previous directory — the next
/// pass re-decides; the loop only converges.
/// </summary>
public sealed class ClusterDirectoryRefreshService(
    ClusterDirectory directory, RedesignControlStore controlStore,
    TimeSpan refreshInterval, TimeSpan staleAfter,
    ILogger<ClusterDirectoryRefreshService>? logger = null) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var reports = await controlStore.ListGameHostReportsAsync(
                    (int)Math.Ceiling(staleAfter.TotalSeconds), stoppingToken);
                var before = directory.GameHostCount;
                directory.ApplyReports(reports, DateTime.UtcNow);
                if (directory.GameHostCount != before)
                    logger?.LogInformation("The game-host directory now holds {Count} live hosts.",
                        directory.GameHostCount);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception error)
            {
                logger?.LogWarning(error, "The game-host directory refresh failed; the previous directory stands.");
            }
            try { await Task.Delay(refreshInterval, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        }
    }
}

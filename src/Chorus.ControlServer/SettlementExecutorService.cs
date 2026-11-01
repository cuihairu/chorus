using Chorus.Rpc;
using Grpc.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Chorus.ControlServer;

/// <summary>
/// The settlement business executor (architecture 9.3, FIX-END-11): the control plane's
/// own sweep that completes a committed manifest's rewards when the data host cannot — the
/// host died before or mid-settle, its reply was lost, its recovery never came. Each pass
/// walks the domains that still hold Finishing rooms, pages the data plane's settle queue,
/// and asks it to apply each listed room's remaining grants. Every grant is its own
/// idempotent receipt transaction, so this executor, the original host and a recoverer can
/// drive the same manifest in any overlap without a double ledger line — the claim and
/// retry discipline on top of that floor is FIX-SETTLE-03's. The data plane stores; it
/// never decides settlement or closing steps: FinalizeRound stays caller-driven and this
/// executor never issues it.
/// </summary>
public sealed class SettlementExecutorService(RedesignControlStore controlStore,
    ClusterDirectory directory, ILogger<SettlementExecutorService> logger) : BackgroundService
{
    private readonly Settlement.SettlementClient _settlement = new(directory.DataChannel);

    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(int.Parse(
        Environment.GetEnvironmentVariable("CHORUS_SETTLE_EXECUTOR_INTERVAL_MS") ?? "2000",
        System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>How many rooms one discovery page carries. Bounded pages keep a pass
    /// proportional to the queue, never to the history behind it (FIX-SETTLE-01).</summary>
    private const int PageSize = 100;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // An interval of zero or less is the operational off switch (maintenance windows;
        // a deployment that deliberately leaves settlement to the admin repair face) — the
        // executor stays idle and says so once.
        if (Interval <= TimeSpan.Zero)
        {
            logger.LogInformation(
                "The settlement executor is disabled: CHORUS_SETTLE_EXECUTOR_INTERVAL_MS is {Ms}.",
                Interval.TotalMilliseconds);
            return;
        }
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                foreach (var domain in await controlStore.ListSettleDomainsAsync(stoppingToken))
                    await SettleDomainAsync(domain, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error)
            {
                logger.LogWarning(error, "Settlement executor pass failed");
            }
            try { await Task.Delay(Interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task SettleDomainAsync(SettleDomain domain, CancellationToken cancellationToken)
    {
        var cursor = "";
        while (cursor is not null)
        {
            var page = await _settlement.ListPendingSettlementsAsync(new ListPendingSettlementsRequest
            {
                TenantId = domain.TenantId, GameId = domain.GameId,
                Limit = PageSize, Cursor = cursor
            }, deadline: DateTime.UtcNow.AddSeconds(5),
                cancellationToken: cancellationToken);
            foreach (var room in page.Settlements)
                await SettleOneAsync(domain, room.RoomId, cancellationToken);
            cursor = page.NextCursor.Length > 0 ? page.NextCursor : null;
        }
    }

    private async Task SettleOneAsync(SettleDomain domain, string roomId,
        CancellationToken cancellationToken)
    {
        try
        {
            var progress = await _settlement.SettleGrantsAsync(new SettleGrantsRequest
            {
                TenantId = domain.TenantId, GameId = domain.GameId, RoomId = roomId
            }, deadline: DateTime.UtcNow.AddSeconds(30),
                cancellationToken: cancellationToken);
            if (progress.GrantsPending == 0)
                logger.LogInformation(
                    "Settled {RoomId}: {Granted} of {Total} grants now stand applied.",
                    roomId, progress.GrantsGranted, progress.GrantsTotal);
        }
        catch (RpcException error) when (error.StatusCode == StatusCode.FailedPrecondition)
        {
            // The queue's truth moved under the walk — the manifest completed elsewhere or
            // the room left Finishing — the next pass re-reads the queue as it stands.
            logger.LogInformation("Settle of {RoomId} was refused as settled elsewhere: {Message}",
                roomId, error.Message);
        }
        catch (RpcException error)
        {
            // A transport fault is the data plane's outage, not this room's: the manifest
            // keeps its committed progress and the next pass rediscovers it.
            logger.LogWarning(error, "Settle of {RoomId} waits for the data plane", roomId);
        }
    }
}

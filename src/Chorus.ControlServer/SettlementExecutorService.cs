using Chorus.Rpc;
using Grpc.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Chorus.ControlServer;

/// <summary>The settlement executor's identity and retry policy (FIX-SETTLE-03): the process
/// instance id that owns a claim lease, the lease length that bounds how long a dead holder
/// can block a room, and the attempt budget before a manifest escalates to NeedsAttention.</summary>
public sealed record SettlementExecutorOptions(
    string InstanceId, TimeSpan LeaseDuration, int MaxAttempts);

/// <summary>
/// The settlement business executor (architecture 9.3, FIX-END-11): the control plane's
/// own sweep that completes a committed manifest's rewards when the data host cannot — the
/// host died before or mid-settle, its reply was lost, its recovery never came. Each pass
/// walks the domains that still hold Finishing rooms, pages the data plane's settle queue,
/// and asks it to apply each listed room's remaining grants. Every grant is its own
/// idempotent receipt transaction, so this executor, the original host and a recoverer can
/// drive the same manifest in any overlap without a double ledger line — the claim and
/// retry discipline on top of that floor is FIX-SETTLE-03's: one claim lease per room
/// serialises concurrent executors, a failure records a bounded backoff (or escalates to
/// NeedsAttention when the budget is spent) on the queryable settle_task row, and a room's
/// fault never stops the walk. The data plane stores; it never decides settlement or closing
/// steps: FinalizeRound stays caller-driven and this executor never issues it.
/// </summary>
public sealed class SettlementExecutorService(RedesignControlStore controlStore,
    ClusterDirectory directory, SettlementExecutorOptions options,
    ILogger<SettlementExecutorService> logger) : BackgroundService
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
        // The claim lease serialises concurrent executors (FIX-SETTLE-03): only one holds a
        // room at a time, and a task the last holder died with is re-claimable once its
        // lease lapses. A claim refused here means another executor owns it, the backoff has
        // not lapsed, or the task needs a human — the next pass re-reads the queue.
        var claimed = await controlStore.ClaimSettleTaskAsync(domain.TenantId, domain.GameId,
            roomId, options.InstanceId, options.LeaseDuration, cancellationToken);
        if (claimed is null)
            return;
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
            // The room's grants are fully applied — the task leaves the table, so a pass stays
            // proportional to in-flight and failed work, never to the history behind it.
            await controlStore.CompleteSettleTaskAsync(roomId, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutting down: the lease lapses on its own and the next process reclaims it —
            // re-calling CommitResult is safe because the per-grant receipts are idempotent.
            throw;
        }
        catch (Exception error)
        {
            // A transport fault, a refused pre-condition or a manifest fault: record it with
            // a bounded backoff on the queryable row. A FailedPrecondition that means "settled
            // elsewhere" self-heals — the next attempt replays as zero pending work and
            // completes. One room's fault never stops the walk (the catch is per room).
            await controlStore.RecordSettleFailureAsync(roomId, options.InstanceId,
                Describe(error), options.MaxAttempts, BackoffFor(claimed.AttemptCount),
                cancellationToken);
            logger.LogWarning(error,
                "Settle of {RoomId} failed on attempt {Attempt} (max {Max}); it backs off and the queue re-lists it.",
                roomId, claimed.AttemptCount, options.MaxAttempts);
        }
    }

    /// <summary>The bounded backoff after attempt N: 1s, 2s, 4s, 8s, 16s, capped at 30s. The
    /// budget is separate — MaxAttempts decides escalation, the backoff only paces it.</summary>
    private static TimeSpan BackoffFor(int attempt) =>
        TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, attempt - 1)));

    private static string Describe(Exception error) =>
        error is RpcException rpc ? $"{rpc.StatusCode}: {rpc.Status.Detail}" : error.Message;
}

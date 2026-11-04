using Chorus.Rpc;
using Grpc.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Chorus.ControlServer;

/// <summary>
/// FIX-SYNC-08: the pause/resume driver (authoritative-sync.md:39-54). Two legs per pass —
/// a resume leg that lifts a paused round whose control participants met the declared ready
/// threshold, and a timeout leg that terminates a paused round whose pause exceeded its
/// declared maximum. The timeout leg runs first so the deadline is honored strictly: a room
/// past its MaxPause is terminated by the module's freeze rule, never lifted by a readiness
/// that arrived too late. Both legs resolve the room's owner in the directory and ask that
/// game host; an unreachable owner leaves the room Paused and the next pass asks again, so a
/// transient outage only ever delays the lift or the termination — never silently drops
/// either. The timeout termination enters the end-operation ladder on the host (FIX-END-15),
/// so it never bypasses the platform's closing confirmation.
/// </summary>
public sealed class PauseResumeCoordinatorService(RedesignControlStore controlStore,
    ClusterDirectory directory, ILogger<PauseResumeCoordinatorService> logger)
    : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(int.Parse(
        Environment.GetEnvironmentVariable("CHORUS_PAUSE_RESUME_INTERVAL_MS") ?? "1000",
        System.Globalization.CultureInfo.InvariantCulture));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                foreach (var room in await controlStore.ListOverduePausedRoomsAsync(stoppingToken))
                    await TryFinishAsync(room, stoppingToken);
                foreach (var room in await controlStore.ListResumeReadyPausedRoomsAsync(stoppingToken))
                    await TryResumeAsync(room, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error)
            {
                logger.LogWarning(error, "Pause/resume coordination pass failed");
            }
            try { await Task.Delay(Interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>Asks the room's owner host to terminate an overdue paused round by the module's
    /// freeze rule. No owner row (a dead owner the takeover net owns) and no live host channel
    /// both leave the room Paused for the next pass — the room is never misjudged as a normal
    /// end.</summary>
    private async Task TryFinishAsync(RedesignControlStore.OverduePausedRoom room,
        CancellationToken cancellationToken)
    {
        var owner = await controlStore.GetOwnershipAsync(room.TenantId, room.GameId, "room",
            room.RoomId, cancellationToken);
        if (owner is null) return; // no owner row: the takeover net owns the room's future
        var channel = directory.GameHostChannel(owner.OwnerInstanceId);
        if (channel is null) return; // the owner left between the snapshot and the resolve
        try
        {
            var reply = await new GameHost.GameHostClient(channel).FinishPausedRoomAsync(
                new FinishPausedRoomRequest
                {
                    TenantId = room.TenantId, GameId = room.GameId, RoomId = room.RoomId,
                    OwnerInstanceId = owner.OwnerInstanceId, Epoch = owner.Epoch,
                    Reason = "pause exceeded MaxPauseSeconds"
                }, deadline: DateTime.UtcNow.AddSeconds(10), cancellationToken: cancellationToken);
            logger.LogInformation(
                "Finished paused {RoomId} by timeout: status {Status}, final tick {Tick}.",
                room.RoomId, reply.Status, reply.FinalTick);
        }
        catch (Exception error) when (error is RpcException or HttpRequestException)
        {
            logger.LogWarning(error, "The timeout termination of {RoomId} waits for its game host: {Message}",
                room.RoomId, error.Message);
        }
    }

    /// <summary>Asks the room's owner host to lift a pause whose ready threshold was met. The
    /// host answers an actor it already holds (no second tick loop) and flips the room to
    /// Running at the frozen baseline; a data-plane refusal leaves the room Paused for the
    /// next pass.</summary>
    private async Task TryResumeAsync(RedesignControlStore.ResumeReadyRoom room,
        CancellationToken cancellationToken)
    {
        var owner = await controlStore.GetOwnershipAsync(room.TenantId, room.GameId, "room",
            room.RoomId, cancellationToken);
        if (owner is null) return; // no owner row: the takeover net owns the room's future
        var channel = directory.GameHostChannel(owner.OwnerInstanceId);
        if (channel is null) return; // the owner left between the snapshot and the resolve
        try
        {
            var reply = await new GameHost.GameHostClient(channel).ResumeRoomAsync(
                new ResumeRoomRequest
                {
                    TenantId = room.TenantId, GameId = room.GameId, RoomId = room.RoomId,
                    OwnerInstanceId = owner.OwnerInstanceId, Epoch = owner.Epoch
                }, deadline: DateTime.UtcNow.AddSeconds(10), cancellationToken: cancellationToken);
            logger.LogInformation("Resumed {RoomId}: status {Status}, actor running {Running}.",
                room.RoomId, reply.Status, reply.ActorRunning);
        }
        catch (Exception error) when (error is RpcException or HttpRequestException)
        {
            logger.LogWarning(error, "The resume of {RoomId} waits for its game host: {Message}",
                room.RoomId, error.Message);
        }
    }
}

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Chorus.ExampleGame;
using Chorus.Rpc;
using Google.Protobuf;
using Grpc.Core;

namespace Chorus.GameServer;

public sealed partial class RoomRuntime
{
    /// <summary>
    /// Applies the room's committed reward manifest. Each grant is its own idempotent
    /// transaction on the data plane; a failure here leaves the manifest's progress
    /// intact and the next call (recovery or a retry) continues where it stopped.
    /// </summary>
    public async Task SettleRoomAsync(string gameId, string roomId, CancellationToken cancellationToken = default)
    {
        try
        {
            await _settlement.CommitResultAsync(new CommitResultRequest
            {
                RoomId = roomId, GameId = gameId, OperationId = roomId + "/settle"
            }, deadline: DateTime.UtcNow.AddSeconds(10), cancellationToken: cancellationToken);
        }
        catch (RpcException)
        {
            // The manifest keeps its committed progress; the room stays inspectable.
        }
    }

    /// <summary>
    /// Step 1's notification (FIX-END-04): the frozen result and its stopped actor belong to
    /// one end operation, begun under the room's own business id. A host without a control
    /// endpoint has no reporter — the control plane's discovery sweep begins the operation
    /// from the room's persisted receipt then.
    /// </summary>
    public async Task ReportEndAsync(string gameId, string roomId, long finalTick,
        long resultVersion, CancellationToken cancellationToken = default)
    {
        if (_endReporter is null) return;
        try
        {
            await _endReporter.ReportFinalAsync(gameId, roomId, finalTick, resultVersion,
                cancellationToken).ConfigureAwait(false);
        }
        catch (RpcException)
        {
            // Best-effort within the bounded retry; the sweep owns what this report lost.
        }
    }

    /// <summary>
    /// FIX-SYNC-06: parks a live room at the committed boundary it just froze — every control
    /// session offline past the declared grace. The pause is a durable store fact, not a
    /// stopped local timer: the round flips Paused and persists the frozen head, so a
    /// process restart restores the paused room from that head without ticking. The operation
    /// id is derived from the room and boundary, so a retry replays the stored receipt.
    /// </summary>
    public async Task PauseRoomAsync(string gameId, string roomId, string ownerInstanceId, long epoch,
        long tick, long resultVersion, string reason, CancellationToken cancellationToken = default)
    {
        try
        {
            await _rounds.PauseRoundAsync(new PauseRoundRequest
            {
                RoomId = roomId, GameId = gameId, OwnerInstanceId = ownerInstanceId, Epoch = epoch,
                Reason = reason, Tick = tick, StateVersion = resultVersion,
                OperationId = $"{ownerInstanceId}/pause/{roomId}/{tick}"
            }, deadline: DateTime.UtcNow.AddSeconds(10), cancellationToken: cancellationToken);
        }
        catch (RpcException)
        {
            // The pause must not silently become a stopped timer that resumes on restart —
            // the room already stopped its loop, so a failed persist parks it visibly instead.
            // The rethrow lets the actor's fail-visible catch suspend the room.
            throw;
        }
    }

    /// <summary>
    /// FIX-SYNC-08: the resume leg — the control plane asks this host to lift a pause once the
    /// readiness threshold is met. Shares the room's activation gate with recovery (a recovery
    /// in flight must be visible before the resume decides the room is unknown), answers an
    /// actor this host already holds (no second tick loop), flips the paused round to Running
    /// on the data plane at the frozen baseline, and only then builds the actor from the
    /// verified head — exactly one, by the same adoption the crash-recovery path uses. A
    /// resume the data plane refuses leaves the room Paused for the next sweep; nothing here
    /// claims a resume that never landed.
    /// </summary>
    public async Task<ResumeRoomReply> ResumeRoomAsync(ResumeRoomRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        Require(request.RoomId, "room_id"); Require(request.GameId, "game_id");
        Require(request.OwnerInstanceId, "owner_instance_id");
        if (request.Epoch <= 0) throw new ArgumentException("The room epoch must be positive.");
        var activation = _activations.GetOrAdd(request.RoomId, static _ => new SemaphoreSlim(1, 1));
        await activation.WaitAsync(cancellationToken);
        try
        {
            if (_actors.TryGetValue(request.RoomId, out var known))
            {
                // An actor this host already holds is the room running — answering it is the
                // no-double-loop guarantee, never a second tick loop.
                var status = known.Status();
                return new ResumeRoomReply
                {
                    RoomId = request.RoomId, Status = DescribeRunState(status.State),
                    ActorRunning = status.State is RoomRunState.Running or RoomRunState.Degraded
                };
            }
            var paused = await LoadWholePlanAsync(request.RoomId, request.GameId,
                RecoveryFirstWindow, cancellationToken);
            if (!paused.Found)
                throw new InvalidOperationException($"The room {request.RoomId} does not exist on this tenant.");
            if (paused.Status != "Paused")
                throw new InvalidOperationException($"A room in status {paused.Status} is not resumable here.");
            if (paused.Epoch != request.Epoch)
                throw new InvalidOperationException($"The room head epoch is {paused.Epoch}, not {request.Epoch}.");
            // FIX-SYNC-08: the data-plane CAS flips Paused→Running at the frozen baseline; a
            // duplicate resume (a retry after a lost reply) replays the stored receipt and
            // reports the already-resumed round — the adoption below is then the no-op
            // "already running" case, never a second actor.
            await _rounds.ResumeRoundAsync(new ResumeRoundRequest
            {
                RoomId = request.RoomId, GameId = request.GameId,
                OwnerInstanceId = request.OwnerInstanceId, Epoch = request.Epoch,
                Tick = paused.LastTick, StateVersion = paused.StateVersion,
                OperationId = $"{request.OwnerInstanceId}/resume/{request.RoomId}/{paused.LastTick}"
            }, deadline: DateTime.UtcNow.AddSeconds(10), cancellationToken: cancellationToken);
            // The flip is durable — reload to observe Running, then adopt exactly one actor
            // from the verified head (the same honest derivation a crash recovery uses).
            var plan = await LoadWholePlanAsync(request.RoomId, request.GameId,
                RecoveryFirstWindow, cancellationToken);
            var (module, refusal) = ResolveRecoveryModule(plan, 0);
            if (module is null) throw new InvalidOperationException(refusal);
            var participantByBinding = plan.Participants
                .Where(participant => participant.BindingId.Length > 0 && participant.State != "Closed")
                .GroupBy(participant => participant.BindingId, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First().ParticipantId, StringComparer.Ordinal);
            var deliveryByEvent = plan.Deliveries
                .Where(delivery => delivery.EventId.Length > 0)
                .GroupBy(delivery => delivery.EventId, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
            var (state, replayed, snapshotTick, pinHead, note, restoreRefusal) =
                await TryRestoreHeadAsync(plan, module, participantByBinding, deliveryByEvent,
                    long.MaxValue, RecoveryFirstWindow,
                    reloadDepth => LoadWholePlanAsync(request.RoomId, request.GameId, reloadDepth,
                        cancellationToken), cancellationToken);
            if (state is null) throw new InvalidOperationException(restoreRefusal);
            // FIX-SYNC-08: a resume of a room that paused at its own head finds the head
            // snapshot already standing (the pause left the room exactly at its newest
            // checkpoint or Prepare boundary) — a re-pin of the same payload would collide
            // on (room_id, tick_id, schema_version). The pin is only for a head tick that is
            // genuinely new (replayed past the newest anchor); an already-pinned head needs
            // no fresh snapshot. This is the resume's own reduction — a crash recovery's pin
            // stays unconditional (FIX-TAKE-02/03's fenced contract).
            var headAlreadyPinned = plan.Snapshots.Any(snapshot =>
                snapshot.Tick == plan.LastTick && snapshot.SchemaVersion == state.SchemaVersion);
            var adopted = await AdoptRecoveredStateAsync(new RecoverRoomRequest
            {
                RoomId = request.RoomId, GameId = request.GameId,
                OwnerInstanceId = request.OwnerInstanceId, Epoch = request.Epoch
            }, plan, module, state, participantByBinding, snapshotTick, replayed, note,
                cancellationToken, pinHead: pinHead && !headAlreadyPinned);
            if (!adopted.Recovered)
                throw new InvalidOperationException(
                    $"The resumed room could not be activated: {adopted.Note}");
            return new ResumeRoomReply
            {
                RoomId = request.RoomId, Status = adopted.Status, ActorRunning = true
            };
        }
        finally
        {
            activation.Release();
        }
    }

    /// <summary>
    /// FIX-SYNC-08: the timeout leg — a paused round exceeded its MaxPause, so the control
    /// plane asks this host to terminate it by the module's freeze rule. The room's head is
    /// restored by the same honest derivation a crash recovery uses, the module derives the
    /// terminal result from the frozen state (no new frame, no tick advance), the data plane
    /// commits that result and flips the room Paused→Finishing — never Settled — and only
    /// then is the end-operation ladder entered (FIX-END-15), so a pause-timeout termination
    /// never bypasses the platform's closing confirmation.
    /// </summary>
    public async Task<FinishPausedRoomReply> FinishPausedRoomAsync(FinishPausedRoomRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        Require(request.RoomId, "room_id"); Require(request.GameId, "game_id");
        Require(request.OwnerInstanceId, "owner_instance_id");
        Require(request.Reason, "reason");
        if (request.Epoch <= 0) throw new ArgumentException("The room epoch must be positive.");
        var plan = await LoadWholePlanAsync(request.RoomId, request.GameId, RecoveryFirstWindow,
            cancellationToken);
        if (!plan.Found)
            throw new InvalidOperationException($"The room {request.RoomId} does not exist on this tenant.");
        if (plan.Status != "Paused")
            throw new InvalidOperationException($"A room in status {plan.Status} has no paused result to finish.");
        if (plan.Epoch != request.Epoch)
            throw new InvalidOperationException($"The room head epoch is {plan.Epoch}, not {request.Epoch}.");
        var (module, refusal) = ResolveRecoveryModule(plan, 0);
        if (module is null) throw new InvalidOperationException(refusal);
        var participantByBinding = plan.Participants
            .Where(participant => participant.BindingId.Length > 0 && participant.State != "Closed")
            .GroupBy(participant => participant.BindingId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().ParticipantId, StringComparer.Ordinal);
        var deliveryByEvent = plan.Deliveries
            .Where(delivery => delivery.EventId.Length > 0)
            .GroupBy(delivery => delivery.EventId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var (state, _, _, _, _, restoreRefusal) =
            await TryRestoreHeadAsync(plan, module, participantByBinding, deliveryByEvent,
                long.MaxValue, RecoveryFirstWindow,
                reloadDepth => LoadWholePlanAsync(request.RoomId, request.GameId, reloadDepth,
                    cancellationToken), cancellationToken);
        if (state is null) throw new InvalidOperationException(restoreRefusal);
        // FIX-SYNC-08: the module's freeze rule derives the terminal result from the frozen
        // state — no new frame, no state hash, no tick advance — and the paused result is the
        // only path to a terminal result for a room that paused.
        var outcome = module.FinishFrozen(state, plan.LastTick, request.Reason);
        var resultJson = outcome.ResultJson
            ?? throw new InvalidOperationException("The freeze rule produced no terminal result.");
        var commit = new CommitPausedResultRequest
        {
            RoomId = request.RoomId, GameId = request.GameId,
            OwnerInstanceId = request.OwnerInstanceId, Epoch = request.Epoch,
            OperationId = $"{request.OwnerInstanceId}/finish-paused/{request.RoomId}/{plan.LastTick}",
            Tick = plan.LastTick,
            PlanHash = ByteString.CopyFrom(SHA256.HashData(Encoding.UTF8.GetBytes(resultJson))),
            ResultJson = resultJson
        };
        // The immutable reward manifest (architecture 9.3) — the identical derivation the
        // final-frame branch applies: each participant earns their faction's frozen score.
        using (var document = JsonDocument.Parse(resultJson))
        {
            var scoreByFaction = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (var entry in document.RootElement.GetProperty("ranking").EnumerateArray())
                scoreByFaction[entry.GetProperty("faction").GetString()!] =
                    entry.GetProperty("score").GetInt64();
            foreach (var (player, faction) in state.FactionByParticipant
                         .OrderBy(pair => pair.Key, StringComparer.Ordinal))
                if (scoreByFaction.TryGetValue(faction, out var score) && score > 0)
                    commit.Grants.Add(new PlannedGrantProto
                    {
                        PlayerId = player, GrantKey = "round-score",
                        OperationId = $"{request.RoomId}/grant/{player}", AssetId = "points",
                        Delta = score
                    });
        }
        var receipt = await _frames.CommitPausedResultAsync(commit, deadline: DateTime.UtcNow.AddSeconds(10),
            cancellationToken: cancellationToken);
        // FIX-SYNC-08: the result is committed and the room is Finishing — now, and only now,
        // the end-operation ladder begins (FIX-END-15): the result report is the ladder's
        // entry, so a pause-timeout termination never bypasses the platform closing tail.
        await ReportEndAsync(request.GameId, request.RoomId, plan.LastTick, 1, cancellationToken);
        return new FinishPausedRoomReply
        {
            RoomId = request.RoomId, Status = "Finishing",
            FinalTick = plan.LastTick, ResultVersion = 1
        };
    }

    /// <summary>FIX-SYNC-06: a control connection established at this generation, routed to
    /// the room's actor. A room this host does not hold (or already paused and retired) has
    /// no actor to feed — the fact is dropped; the room's own state decides.</summary>
    public void ReportControlConnected(string roomId, long generation, string identity, PresenceLinkKind kind)
    {
        FindRoom(roomId)?.ReportControlConnected(generation, identity, kind);
    }

    /// <summary>FIX-SYNC-06: a control session ended, routed to the room's actor.</summary>
    public void ReportControlDisconnected(string roomId, long generation, string identity, ControlLinkReason reason)
    {
        FindRoom(roomId)?.ReportControlDisconnected(generation, identity, reason);
    }

    /// <summary>FIX-SYNC-06: a control keepalive, routed to the room's actor.</summary>
    public void ReportControlHeartbeat(string roomId, long generation, string identity)
    {
        FindRoom(roomId)?.ReportControlHeartbeat(generation, identity);
    }

    /// <summary>
    /// Parks a damaged room visibly. The operation id is derived from the room, so a repeated
    /// suspension replays the stored receipt; a room already released or finished simply
    /// rejects — nothing here may take the host down with it.
    /// </summary>
    public async Task SuspendRoomAsync(string gameId, string roomId, string ownerInstanceId, long epoch,
        string reason, CancellationToken cancellationToken = default)
    {
        try
        {
            await _rounds.SuspendRoundAsync(new SuspendRoundRequest
            {
                RoomId = roomId, GameId = gameId, OwnerInstanceId = ownerInstanceId, Epoch = epoch,
                Reason = reason, OperationId = $"{ownerInstanceId}/suspend/{roomId}"
            }, deadline: DateTime.UtcNow.AddSeconds(10), cancellationToken: cancellationToken);
        }
        catch (RpcException)
        {
            // The suspension itself is best-effort from inside a failing actor; the room's
            // state stays inspectable on the data plane either way.
        }
    }
}

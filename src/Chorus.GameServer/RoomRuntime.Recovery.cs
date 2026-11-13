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
    /// <summary>The first candidate window a recovery reads (FIX-READ-04): the fallback
    /// pair, mirroring the store's normal bounded load — the pair retention keeps.</summary>
    private const int RecoveryFirstWindow = 2;

    /// <summary>The deepest candidate window a recovery reads on demand (FIX-READ-04): a
    /// safety bound so pathological history cannot deepen the read forever; past it the
    /// fallback ends and the room parks.</summary>
    private const int RecoveryDeepestWindow = 512;

    /// <summary>
    /// Recovers a room this process does not know (architecture 10.3): load the recovery plan,
    /// walk the newest usable snapshot forward through the committed frames — verifying every
    /// frame's state hash on the way — and resume ticking from the head. A corrupt or
    /// inconsistent newest snapshot falls back to the previous one — and when a whole
    /// candidate window fails, the read deepens on demand instead of parking while older
    /// anchors remain (FIX-READ-04); when no snapshot replays to the head the room is
    /// suspended visibly. A Finishing room recovers its tail without restarting battle
    /// ticks. Replay is read-only: frames and effects were committed once and are never
    /// re-triggered.
    /// </summary>
    public async Task<RecoverRoomReply> RecoverRoomAsync(RecoverRoomRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        Require(request.RoomId, "room_id"); Require(request.GameId, "game_id");
        Require(request.OwnerInstanceId, "owner_instance_id");
        if (request.Epoch <= 0) throw new ArgumentException("The room epoch must be positive.");
        // FIX-MODULE-01: 0 means "use the room's persisted cadence" — the cadence is a room
        // fact, never a caller opinion. A positive value is still validated and must match
        // the persisted cadence (the identity check below enforces the match).
        if (request.TickMs is < 0 or > 1000)
            throw new ArgumentException("The tick cadence is 0..1000 ms; 0 means the room's persisted cadence.");
        // FIX-ROOM-03: recovery shares the room's activation gate with prepare — a prepare
        // in flight must be visible here before recovery decides the room is unknown.
        var activation = _activations.GetOrAdd(request.RoomId, static _ => new SemaphoreSlim(1, 1));
        await activation.WaitAsync(cancellationToken);
        try
        {
            if (_actors.TryGetValue(request.RoomId, out var known))
            {
                // FIX-ROOM-02: an entry in the dictionary is not liveness — the answer derives
                // from the actor's own state, never from the dictionary alone.
                return AnswerForKnownActor(request, known.Status());
            }
            // FIX-PERF-08: the heavy work of a takeover — the plan load and the frame replay
            // that dominate a mass recovery's memory — is bounded to a set number of
            // concurrent recoveries; the rest wait (the queue distribution is measured), and a
            // recovery past the queue is refused explicitly instead of being left to OOM the
            // host. The slot is held only for the recovery work, not for the room's lifetime:
            // once the actor is running, the active-room admission budget (FIX-PERF-07) owns
            // the memory. A recovery never reserves an admission slot until it actually lands,
            // so a refused recovery leaks nothing.
            if (_recoveryGate is { } gate)
            {
                var queueStart = _metrics.Start();
                using var lease = await gate.EnterAsync(cancellationToken);
                _metrics.Record(_metrics.RecoveryQueue, queueStart);
                return await RecoverUnknownRoomAsync(request, cancellationToken);
            }
            return await RecoverUnknownRoomAsync(request, cancellationToken);
        }
        finally
        {
            activation.Release();
        }
    }

    /// <summary>Recovery of a room no local actor holds: load the plan, replay to the head,
    /// pin the verified head through the constrained recovery entry, activate by the fenced
    /// confirm and only then resume ticking (FIX-TAKE-03). The caller holds the room's
    /// activation gate throughout.</summary>
    /// <summary>Recovery of a room no local actor holds: load the plan, replay to the head,
    /// pin the verified head through the constrained recovery entry, activate by the fenced
    /// confirm and only then resume ticking (FIX-TAKE-03). The caller holds the room's
    /// activation gate throughout.</summary>
    private async Task<RecoverRoomReply> RecoverUnknownRoomAsync(RecoverRoomRequest request,
        CancellationToken cancellationToken)
    {
        // Candidates load window-first, and a window that fails refutes only itself
        // (FIX-READ-04): older anchors may still replay, so each failed window deepens the
        // read on demand — doubling, so deep history costs logarithmic reads — instead of
        // suspending while untried anchors remain. A reply carrying fewer candidates than
        // the window asked for has already shown the whole history; there, or at the safety
        // cap, the fallback ends and the room parks.
        var depth = RecoveryFirstWindow;
        var refutedAbove = long.MaxValue; // candidates at or above this tick already failed
        var plan = await LoadWholePlanAsync(request.RoomId, request.GameId, depth, cancellationToken);
        {
            if (!plan.Found)
                return new RecoverRoomReply
                {
                    RoomId = request.RoomId, Recovered = false,
                    Note = "The room does not exist on this tenant."
                };
            if (plan.Epoch != request.Epoch)
                return new RecoverRoomReply
                {
                    RoomId = request.RoomId, Recovered = false, Status = plan.Status,
                    Note = $"The room head epoch is {plan.Epoch}, not {request.Epoch}."
                };
            // FIX-MODULE-01: the room's identity is a persisted fact, never a caller
            // opinion — the recovery request cannot change the original round's tick or
            // module by restating it, and a room whose package, version or checkpoint mode
            // this host cannot load is refused by name instead of silently rebuilt from
            // hardcoded literals. Validated before any module is built or replay starts.
            // FIX-MODULE-02: "what this host loads" is the package registry — every
            // package it ships, not the one hardcoded identity.
            // FIX-MODULE-01/02 and FIX-SYNC-04: the room's identity facts (package, module
            // version, recovery mode, offline policy) are persisted facts, never caller
            // opinions — the shared resolution below is what both a crash recovery and the
            // pause-timeout termination use, so the two paths can never disagree about which
            // module owns a room. A refusal names the exact fact that failed.
            var (module, moduleRefusal) = ResolveRecoveryModule(plan, request.TickMs);
            if (module is null)
                return new RecoverRoomReply
                {
                    RoomId = request.RoomId, Recovered = false, Status = plan.Status,
                    Note = moduleRefusal
                };
            // FIX-SYNC-06: a paused room recovers to Paused — the freeze is a durable store
            // fact, so the recovery confirms this host owns the paused room (the same fenced
            // confirm a running recovery takes) but starts no tick loop and replays nothing.
            // The frozen head stays the boundary a later resume (SYNC-08) lifts; nothing here
            // may tick past it. The identity above is still validated: the pause never
            // suspends the room's module facts.
            if (plan.Status == "Paused")
            {
                var activation = await ActivateRecoveredRoomAsync(request, plan, cancellationToken);
                if (activation is not null) return activation;
                return new RecoverRoomReply
                {
                    RoomId = request.RoomId, Recovered = true, Status = "Paused",
                    LastTick = plan.LastTick,
                    Note = "The round stays paused at its committed boundary; it is frozen, not running."
                };
            }
            // Finishing rooms recover their result and settlement tail only; battle ticks are
            // never restarted for a round that already froze its result. FIX-TAKE-03: the
            // closing tail activates under the same fenced confirm before any settlement
            // effect — a recoverer that no longer holds the room neither claims the tail nor
            // settles it. The settle call is idempotent per grant (9.3).
            if (plan.Status == "Finishing")
            {
                var activation = await ActivateRecoveredRoomAsync(request, plan, cancellationToken);
                if (activation is not null) return activation;
                await SettleRoomAsync(request.GameId, request.RoomId, cancellationToken);
                // The recovery of a finished round is also the reliable report (FIX-END-04):
                // the result that froze on the dead host is begun under the same business id —
                // a report the dead host already landed replays one row.
                await ReportEndAsync(request.GameId, request.RoomId, plan.LastTick,
                    plan.StateVersion, cancellationToken).ConfigureAwait(false);
                return new RecoverRoomReply
                {
                    RoomId = request.RoomId, Recovered = true, Status = plan.Status,
                    LastTick = plan.LastTick,
                    Note = "The round is finishing: the result tail recovers without battle ticks."
                };
            }
            if (plan.Status != "Running")
                return new RecoverRoomReply
                {
                    RoomId = request.RoomId, Recovered = false, Status = plan.Status,
                    Note = $"A room in status {plan.Status} is not recoverable here."
                };
            var participantByBinding = plan.Participants
                .Where(participant => participant.BindingId.Length > 0 && participant.State != "Closed")
                .GroupBy(participant => participant.BindingId, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First().ParticipantId, StringComparer.Ordinal);
            var deliveryByEvent = plan.Deliveries
                .Where(delivery => delivery.EventId.Length > 0)
                .GroupBy(delivery => delivery.EventId, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

            // FIX-SYNC-08: the head-state derivation is the shared honest replay (a crash
            // recovery and the pause-timeout termination restore the same verified state).
            // A refusal names the cause and parks the room; a success hands the verified
            // head state to adoption, which starts exactly one actor for this host.
            var (state, replayed, snapshotTick, pinHead, note, refusal) =
                await TryRestoreHeadAsync(plan, module, participantByBinding, deliveryByEvent,
                    refutedAbove, depth,
                    reloadDepth => LoadWholePlanAsync(request.RoomId, request.GameId, reloadDepth,
                        cancellationToken),
                    cancellationToken);
            if (state is null)
                return await RefuseAndSuspendAsync(request, plan, refusal!, cancellationToken);
            return await AdoptRecoveredStateAsync(request, plan, module, state, participantByBinding,
                snapshotTick, replayed, note, cancellationToken, pinHead: pinHead,
                deferActivation: request.DeferredActivation);
        }
    }

    /// <summary>
    /// Resolves the module that owns a room from its persisted plan (FIX-MODULE-01/02,
    /// FIX-SYNC-04): the room's identity facts — package, module version, recovery mode,
    /// offline policy — are persisted facts, never caller opinions, so a recovery or a
    /// pause-timeout termination cannot restate them. Returns the loaded module, or a
    /// refusal naming the exact persisted fact that failed. <paramref name="requestedTickMs"/>
    /// is 0 for "use the room's persisted cadence"; a positive value must match it.
    /// </summary>
    private (IRedesignGameModule? Module, string? Refusal) ResolveRecoveryModule(
        LoadRecoveryReply plan, int requestedTickMs)
    {
        if (plan.TickMs is < 1 or > 1000)
            return (null, $"The room's persisted cadence {plan.TickMs} ms is invalid.");
        if (requestedTickMs > 0 && requestedTickMs != plan.TickMs)
            return (null, $"The recovery cadence {requestedTickMs} ms does not match the room's " +
                $"persisted cadence {plan.TickMs} ms; a recovery cannot change a room's tick.");
        if (!TryResolvePackage(plan.PackageHash, out var package))
            return (null, $"The room's package \"{PackageNameFor(plan.PackageHash)}\" is not a " +
                $"package this host ships (shipped: {_shippedList}); the package is missing or unknown.");
        if (plan.ModuleVersion != package.ModuleVersion)
            return (null, $"The room's module version \"{plan.ModuleVersion}\" is not the version " +
                $"package \"{package.PackageHash}\" ships (\"{package.ModuleVersion}\"); the module is incompatible.");
        if (plan.RecoveryMode is not (RedesignGameModuleManifest.DeterministicReplay
                or RedesignGameModuleManifest.FullCheckpoint))
            return (null, $"The room's recovery mode \"{plan.RecoveryMode}\" is not a known mode.");
        if (plan.RecoveryMode != package.RecoveryMode)
            return (null, $"The room's recovery mode \"{plan.RecoveryMode}\" is not the mode " +
                $"package \"{package.PackageHash}\" ships (\"{package.RecoveryMode}\"); the module is incompatible.");
        // FIX-SYNC-04: the room's offline policy is a persisted identity fact, never a
        // caller opinion and never silently refreshed from the package. A room whose stored
        // declaration no longer matches what the package ships is refused by name, so a live
        // round's offline behavior cannot change mid-flight.
        var shippedOffline = package.ManifestAt(plan.TickMs).Offline;
        if (shippedOffline is null || plan.OfflinePolicy != shippedOffline.Canonical)
            return (null, $"The room's offline policy \"{plan.OfflinePolicy}\" is not the " +
                $"policy package \"{package.PackageHash}\" ships " +
                $"(\"{shippedOffline?.Canonical ?? "none"}\"); the round's offline behavior cannot change mid-flight.");
        return (package.Load(package.ManifestAt(plan.TickMs)), null);
    }

    /// <summary>
    /// Restores the room's head state from its committed plan (FIX-SYNC-08 hoisted the
    /// shared derivation here so a crash recovery and the pause-timeout termination use the
    /// same honest replay): a FullCheckpoint room restores its head checkpoint directly, a
    /// deterministic-replay room replays the newest surviving snapshot to the head. Returns
    /// the verified head state, the anchor snapshot tick and the replay count; a refusal (an
    /// unreadable checkpoint, a hash mismatch, no snapshot replays) names the cause and the
    /// caller parks the room. Never starts an actor — adoption is the caller's.
    /// </summary>
    private async Task<(ExampleGameState? State, int Replayed, long SnapshotTick, bool PinHead, string Note, string? Refusal)>
        TryRestoreHeadAsync(LoadRecoveryReply plan, IRedesignGameModule module,
        Dictionary<string, string> participantByBinding,
        Dictionary<string, RecoveryDeliveryProto> deliveryByEvent,
        long refutedAbove, int depth, Func<int, Task<LoadRecoveryReply>> reloadPlan,
        CancellationToken cancellationToken)
    {
        if (module.Manifest.CheckpointMode == RedesignGameModuleManifest.FullCheckpoint)
        {
            var head = plan.Snapshots.Where(candidate => candidate.Tick <= plan.LastTick)
                .OrderByDescending(candidate => candidate.Tick).FirstOrDefault();
            if (head is null || head.Tick != plan.LastTick)
                return (null, 0, 0, false, "",
                    "The room's newest checkpoint is at tick " + (head?.Tick.ToString() ?? "none")
                    + $", not the head tick {plan.LastTick}; a FullCheckpoint room cannot replay to the head");
            ExampleGameState checkpointed;
            try { checkpointed = module.Restore(head.Payload.ToByteArray(), head.SchemaVersion); }
            catch (Exception)
            {
                return (null, 0, 0, false, "",
                    "The room's head checkpoint is unreadable; a FullCheckpoint room cannot replay to the head");
            }
            if (!module.ComputeStateHash(checkpointed).AsSpan().SequenceEqual(head.PayloadHash.Span))
                return (null, 0, 0, false, "",
                    "The room's head checkpoint failed its hash check; a FullCheckpoint room cannot replay to the head");
            return (checkpointed, 0, head.Tick, false,
                $"Restored the tick-{head.Tick} checkpoint; a FullCheckpoint room resumes without replay.", null);
        }

        while (true)
        {
            foreach (var candidate in plan.Snapshots.Where(candidate => candidate.Tick < refutedAbove))
            {
                if (candidate.Tick > plan.LastTick) continue;
                var replayStart = _metrics.Start();
                ExampleGameState state;
                try { state = module.Restore(candidate.Payload.ToByteArray(), candidate.SchemaVersion); }
                catch (Exception) { continue; } // unreadable snapshot: try the previous one
                if (!module.ComputeStateHash(state).AsSpan().SequenceEqual(candidate.PayloadHash.Span))
                    continue; // corrupt payload: hash mismatch is evidence, not a guess

                var consistent = true;
                var replayed = 0;
                foreach (var frame in plan.Frames.Where(frame => frame.Tick > candidate.Tick)
                    .OrderBy(frame => frame.Tick))
                {
                    if (frame.Tick != state.Tick + 1) { consistent = false; break; }
                    var inputs = new List<NormalizedInput>();
                    foreach (var eventId in frame.InputEventIds)
                        if (deliveryByEvent.TryGetValue(eventId, out var delivery)
                            && delivery.BindingId is { Length: > 0 } binding
                            && participantByBinding.TryGetValue(binding, out var participant))
                        {
                            // FIX-INPUT-08: the same translation the live tick applied when it
                            // committed this frame — a replayed room must re-derive the command
                            // the original tick fed the module, or the state hash diverges.
                            // FIX-SYNC-09: the origin rides with the delivery (the recovery plan
                            // now carries the platform channel) so the replay applies the same
                            // origin-aware gate the live tick did — a client command stays raw
                            // and the module's intent-only rule ignores a forged platform shape.
                            // FIX-GM-07: the shared predicate is what keeps that agreement exact
                            // for the delegated GM channel too.
                            var fromClient = RoomCommandPlatforms.IsRawCommand(delivery.Platform);
                            var command = fromClient ? delivery.PayloadJson
                                : _inputTranslator?.Translate(delivery.PayloadJson) ?? delivery.PayloadJson;
                            if (command is not null)
                                inputs.Add(new NormalizedInput(participant, command, fromClient));
                        }
                    TickOutcome outcome;
                    try { outcome = module.ApplyTick(state, frame.Tick, inputs); }
                    catch (Exception) { consistent = false; break; }
                    if (!module.ComputeStateHash(outcome.State).AsSpan().SequenceEqual(frame.StateHash.Span))
                    {
                        consistent = false;
                        break;
                    }
                    state = outcome.State;
                    replayed++;
                }
                _metrics.Record(_metrics.RecoveryReplay, replayStart);
                if (!consistent || state.Tick != plan.LastTick) continue;

                return (state, replayed, candidate.Tick, true,
                    $"Replayed {replayed} frames from the tick-{candidate.Tick} snapshot.", null);
            }

            // This window is refuted down to its oldest loaded candidate.
            refutedAbove = plan.Snapshots.Count > 0
                ? plan.Snapshots.Min(candidate => candidate.Tick) : 0;
            if (plan.Snapshots.Count < depth || depth >= RecoveryDeepestWindow)
                break;
            depth *= 2;
            // FIX-SYNC-08: a deeper window needs its own plan load — the original loop
            // reloaded the whole plan at each deepening, and a reload refreshes the
            // snapshot window the next iteration reads.
            plan = await reloadPlan(depth);
        }
        return (null, 0, 0, false, "", "No snapshot replays to the head with matching frame hashes");
    }

    /// <summary>
    /// Adopts a recovered state as this host's running room (FIX-MODULE-03 hoisted the
    /// adoption tail here so the replay path and the FullCheckpoint restore path share
    /// one activation story): pin the head snapshot, activate under the fenced confirm,
    /// and start the actor from the recovered tick. Returns the reply the caller hands
    /// back — including the not-recovered replies an activation or a pin rejection
    /// produced.
    /// </summary>
    /// <param name="pinHead">A replayed state persists a fresh store-verified snapshot
    /// at the head; a FullCheckpoint restore passes false — its head checkpoint is
    /// already persisted, every committed frame carried it, so there is nothing to
    /// pin.</param>
    private async Task<RecoverRoomReply> AdoptRecoveredStateAsync(RecoverRoomRequest request,
        LoadRecoveryReply plan, IRedesignGameModule module, ExampleGameState state,
        Dictionary<string, string> participantByBinding, long snapshotTick, int replayed,
        string note, CancellationToken cancellationToken, bool pinHead = true,
        bool deferActivation = false)
    {
        var headPayload = module.Serialize(state); // the config fallback below either way
        // Persist a fresh store-verified snapshot at the head so the next recovery starts
        // here. FIX-TAKE-02/03: while the ownership is Recovering the pin travels the
        // constrained recovery entry — exactly the takeover-named owner at this epoch
        // passes — and once that entry closed (the activation already happened, say a retry
        // after a lost reply) the normal snapshot entry carries the same payload. A resume
        // passes a pinHead already reduced by the caller (FIX-SYNC-08): a room that paused
        // at its own head carries the head snapshot already, so there is nothing to pin and
        // a re-pin would collide on (room_id, tick_id, schema_version).
        if (pinHead)
        {
            // Persist a fresh store-verified snapshot at the head so the next recovery
            // starts here. FIX-TAKE-02/03: while the ownership is Recovering the pin
            // travels the constrained recovery entry — exactly the takeover-named
            // owner at this epoch passes — and once that entry closed (the activation
            // already happened, say a retry after a lost reply) the normal snapshot
            // entry carries the same payload.
            try
            {
                await PinHeadAsync(request, plan.LastTick, headPayload, state.SchemaVersion,
                    cancellationToken);
            }
            catch (RpcException error)
            {
                return new RecoverRoomReply
                {
                    RoomId = request.RoomId, Recovered = false, Status = plan.Status,
                    LastTick = plan.LastTick,
                    Note = $"The head snapshot was rejected: {error.Status.Detail}"
                };
            }
        }
        // FIX-REC-01 deferred recovery (FIX-TAKE-03 grant, moved to the release): the
        // pinned head stays behind the Recovering fence — no confirm, no loop — so the
        // control plane may install the gateways' routes and only then grant the
        // activation. The recovery credential is what AdoptRecoveredStateAsync persisted:
        // the head pin plus this held actor; ActivateRoomAsync's confirm is the grant, and
        // a lost-reply retry answers Ready from the held actor instead of redoing work.
        if (deferActivation)
        {
            var held = new RoomActor(this, _frames, module, request.GameId, request.RoomId,
                request.OwnerInstanceId, request.Epoch, HeadConfigHash(plan, headPayload), state,
                participantByBinding, plan.LastTick, plan.StateVersion, metrics: _metrics,
                overBudgetFactor: _overBudgetFactorOverride,
                overBudgetWindow: _overBudgetWindowOverride, inboundBound: _inboundBound,
                inputTranslator: _inputTranslator, counters: EventCounters, heldFromRecovery: true);
            held.LoopEnded = Retire;
            // FIX-PERF-07: a held recovered room is real load this host must run — a
            // recovery is never refused (the scheduler assigned it here) — but it still
            // reserves a slot, so a host full of recovered rooms stops accepting new
            // allocations.
            _admission.Reserve();
            if (!_actors.TryAdd(request.RoomId, held))
            {
                _admission.Release();
                await held.DisposeAsync();
                return AnswerForKnownActor(request, _actors[request.RoomId].Status());
            }
            held.HoldForCommonStart();
            return new RecoverRoomReply
            {
                RoomId = request.RoomId, Recovered = true, Status = "Ready",
                LastTick = plan.LastTick, SnapshotTick = snapshotTick,
                ReplayedFrames = replayed,
                Note = $"{note} The room is held: the loop starts when the control plane " +
                       "activates it after the route barrier."
            };
        }
        // FIX-TAKE-03: the pinned head is the activation credential — the fenced
        // confirm flips the ownership Active for exactly this owner and epoch before
        // the loop starts, so a recovery that cannot activate neither ticks nor
        // reports success.
        var activated = await ActivateRecoveredRoomAsync(request, plan, cancellationToken);
        if (activated is not null) return activated;
        var configHash = HeadConfigHash(plan, headPayload);
        var actor = new RoomActor(this, _frames, module, request.GameId, request.RoomId,
            request.OwnerInstanceId, request.Epoch, configHash, state, participantByBinding,
            plan.LastTick, plan.StateVersion, metrics: _metrics,
            overBudgetFactor: _overBudgetFactorOverride,
            overBudgetWindow: _overBudgetWindowOverride, inboundBound: _inboundBound,
            inputTranslator: _inputTranslator, counters: EventCounters);
        actor.LoopEnded = Retire;
        // FIX-PERF-07: a recovered room is real load this host must run — a recovery
        // is never refused (the scheduler assigned it here) — but it still reserves a
        // slot, so a host full of recovered rooms stops accepting new allocations.
        _admission.Reserve();
        if (!_actors.TryAdd(request.RoomId, actor))
        {
            _admission.Release();
            await actor.DisposeAsync();
            return new RecoverRoomReply
            {
                RoomId = request.RoomId, Recovered = true, Status = "Running",
                Note = "The room is already running on this host."
            };
        }
        actor.Start();
        _activated.TryAdd(request.RoomId, 0);
        return new RecoverRoomReply
        {
            RoomId = request.RoomId, Recovered = true, Status = "Running",
            LastTick = plan.LastTick, SnapshotTick = snapshotTick,
            ReplayedFrames = replayed,
            Note = note
        };
    }

    /// <summary>The config hash a recovered room's frames carry — the committed head
    /// frame's own hash when it exists, else the head payload's.</summary>
    private static string HeadConfigHash(LoadRecoveryReply plan, byte[] headPayload) =>
        plan.Frames.Count > 0
            ? plan.Frames.Max(frame => frame.Tick) == plan.LastTick
                ? plan.Frames.First(frame => frame.Tick == plan.LastTick).ConfigHash
                : Convert.ToHexString(SHA256.HashData(headPayload)).ToLowerInvariant()
            : Convert.ToHexString(SHA256.HashData(headPayload)).ToLowerInvariant();

    /// <summary>
    /// Refuses a recovery by name and parks the room visibly (architecture 10.3.6):
    /// a suspension is attempted best-effort, and when the fence refuses it — a
    /// Recovering ownership does (FIX-TAKE-03) — the reply says so instead of claiming
    /// a suspension that never landed.
    /// </summary>
    private async Task<RecoverRoomReply> RefuseAndSuspendAsync(RecoverRoomRequest request,
        LoadRecoveryReply plan, string reason, CancellationToken cancellationToken)
    {
        try
        {
            await _rounds.SuspendRoundAsync(new SuspendRoundRequest
            {
                RoomId = request.RoomId, GameId = request.GameId,
                OwnerInstanceId = request.OwnerInstanceId, Epoch = request.Epoch,
                Reason = $"recovery failed: {reason}",
                OperationId = $"{request.OwnerInstanceId}/suspend/{request.RoomId}"
            }, deadline: DateTime.UtcNow.AddSeconds(10), cancellationToken: cancellationToken);
            return new RecoverRoomReply
            {
                RoomId = request.RoomId, Recovered = false, Status = "Suspended",
                Note = $"{reason}; the room is suspended."
            };
        }
        catch (RpcException)
        {
            // Best-effort from inside a failing recovery; the room stays inspectable on
            // the data plane either way.
            return new RecoverRoomReply
            {
                RoomId = request.RoomId, Recovered = false, Status = plan.Status,
                LastTick = plan.LastTick,
                Note = $"{reason}; the suspension was refused and the ownership stays Recovering."
            };
        }
    }

    /// <summary>
    /// The whole recovery plan of one view, page by page (FIX-READ-05): each reply carries
    /// a bounded slice — an explicit row and byte limit, never a grown message ceiling —
    /// and the cursor of the next, with the head fields riding on every page. Assembly
    /// refuses a page whose head or epoch differs from the first (the server already
    /// refuses to continue a moved view; this is the client-side check), and a reader
    /// that stops asking for the next page cancels the read on its own — no page holds
    /// server state.
    /// </summary>
    private async Task<LoadRecoveryReply> LoadWholePlanAsync(string roomId, string gameId,
        int depth, CancellationToken cancellationToken)
    {
        // FIX-PERF-01: one recovery window's load, pages and all, is a phase of its own.
        var readStart = _metrics.Start();
        try
        {
            return await LoadWholePlanInnerAsync(roomId, gameId, depth, cancellationToken);
        }
        finally
        {
            _metrics.Record(_metrics.RecoveryRead, readStart);
        }
    }

    private async Task<LoadRecoveryReply> LoadWholePlanInnerAsync(string roomId, string gameId,
        int depth, CancellationToken cancellationToken)
    {
        LoadRecoveryReply whole = new();
        ByteString next = ByteString.Empty;
        var first = true;
        do
        {
            var page = await _frames.LoadRecoveryAsync(new LoadRecoveryRequest
            {
                RoomId = roomId, GameId = gameId, CandidateDepth = depth, PageToken = next
            }, deadline: DateTime.UtcNow.AddSeconds(10), cancellationToken: cancellationToken);
            if (!page.Found) return page;
            if (!first && (page.Epoch != whole.Epoch || page.LastTick != whole.LastTick))
                throw new RpcException(new Status(StatusCode.FailedPrecondition,
                    "The recovery view moved between pages; restart the read."));
            if (first)
            {
                whole = new LoadRecoveryReply
                {
                    Found = true, RoomId = page.RoomId, Status = page.Status, Epoch = page.Epoch,
                    LastTick = page.LastTick, StateVersion = page.StateVersion,
                    TickMs = page.TickMs, ModuleVersion = page.ModuleVersion,
                    ConfigHash = page.ConfigHash, PackageHash = page.PackageHash,
                    RecoveryMode = page.RecoveryMode, OfflinePolicy = page.OfflinePolicy
                };
                first = false;
            }
            whole.Snapshots.AddRange(page.Snapshots);
            whole.Deliveries.AddRange(page.Deliveries);
            whole.Frames.AddRange(page.Frames);
            whole.Participants.AddRange(page.Participants);
            next = page.NextPageToken;
        } while (next.Length > 0);
        return whole;
    }

    /// <summary>
    /// Pins the store-verified head snapshot (FIX-TAKE-02/03): a takeover recovery writes
    /// through the constrained recovery entry while the ownership is Recovering; once the
    /// ownership is Active again — the activation already happened, or this is a plain
    /// restart recovery — the normal snapshot entry carries the same payload. Both entries
    /// derive their operation id from the owner, room and tick, so a retry replays the
    /// stored receipt on either side of the activation boundary.
    /// </summary>
    private async Task PinHeadAsync(RecoverRoomRequest request, long tick, byte[] payload,
        int schemaVersion, CancellationToken cancellationToken)
    {
        try
        {
            await _frames.PutRecoverySnapshotAsync(new PutRecoverySnapshotRequest
            {
                RoomId = request.RoomId, GameId = request.GameId,
                OwnerInstanceId = request.OwnerInstanceId, Epoch = request.Epoch, Tick = tick,
                SnapshotPayload = ByteString.CopyFrom(payload),
                SnapshotHash = ByteString.CopyFrom(SHA256.HashData(payload)),
                SnapshotSchemaVersion = schemaVersion,
                OperationId = $"{request.OwnerInstanceId}/recovery-snapshot/{request.RoomId}/{tick}"
            }, deadline: DateTime.UtcNow.AddSeconds(10), cancellationToken: cancellationToken);
        }
        catch (RpcException error) when (error.StatusCode == StatusCode.FailedPrecondition &&
            error.Status.Detail == "The recovery snapshot fence rejected the caller.")
        {
            await _frames.PutSnapshotAsync(new PutSnapshotRequest
            {
                RoomId = request.RoomId, GameId = request.GameId,
                OwnerInstanceId = request.OwnerInstanceId, Epoch = request.Epoch, Tick = tick,
                SnapshotPayload = ByteString.CopyFrom(payload),
                SnapshotHash = ByteString.CopyFrom(SHA256.HashData(payload)),
                SnapshotSchemaVersion = schemaVersion,
                OperationId = $"{request.OwnerInstanceId}/snapshot/{request.RoomId}/{tick}"
            }, deadline: DateTime.UtcNow.AddSeconds(10), cancellationToken: cancellationToken);
        }
    }

    /// <summary>
    /// The fenced activation of a completed recovery (FIX-TAKE-03): the recoverer confirms
    /// the ownership for exactly its instance — idempotent once the room is Active — before
    /// any settlement call or tick loop starts. A refusal returns the failure reply; the
    /// room is then neither activated, settled nor ticking, and no caller can mistake the
    /// ownership for a ready room.
    /// </summary>
    private async Task<RecoverRoomReply?> ActivateRecoveredRoomAsync(RecoverRoomRequest request,
        LoadRecoveryReply plan, CancellationToken cancellationToken)
    {
        // FIX-PERF-08: the fenced confirm is a recovery phase of its own — the distribution
        // ships with the read and replay spans in the mass-recovery report.
        var activateStart = _metrics.Start();
        try
        {
            await _rounds.ConfirmRoomOwnershipAsync(new ConfirmRoomOwnershipRequest
            {
                GameId = request.GameId, RoomId = request.RoomId,
                OwnerInstance = request.OwnerInstanceId
            }, deadline: DateTime.UtcNow.AddSeconds(10), cancellationToken: cancellationToken);
            _metrics.Record(_metrics.RecoveryActivate, activateStart);
            return null;
        }
        catch (RpcException error)
        {
            return new RecoverRoomReply
            {
                RoomId = request.RoomId, Recovered = false, Status = plan.Status,
                LastTick = plan.LastTick,
                Note = $"The activation was refused: {error.Status.Detail}"
            };
        }
    }

    /// <summary>
    /// The recovery answer for a room whose actor this process already holds (FIX-ROOM-02).
    /// A dictionary entry is not liveness: only an actor of the requested epoch whose loop is
    /// actually running answers Running; a completed actor of the same epoch answers the
    /// Finishing tail; everything else — stopped, disposed, or holding a stale epoch — refuses
    /// with the observed state and the real committed head.
    /// </summary>
    internal static RecoverRoomReply AnswerForKnownActor(RecoverRoomRequest request, RoomActorStatus status)
    {
        if (status.Epoch != request.Epoch)
            return new RecoverRoomReply
            {
                RoomId = request.RoomId, Recovered = false, Status = DescribeRunState(status.State),
                LastTick = status.LastCommittedTick,
                Note = $"The local actor holds epoch {status.Epoch}, not {request.Epoch}; " +
                       "the room head lives with a newer owner."
            };
        if (status.State == RoomRunState.Completed)
            return new RecoverRoomReply
            {
                RoomId = request.RoomId, Recovered = true, Status = "Finishing",
                LastTick = status.LastCommittedTick,
                Note = "The round already finished on this host; the result tail needs no battle ticks."
            };
        if (status.State == RoomRunState.Running)
            return new RecoverRoomReply
            {
                RoomId = request.RoomId, Recovered = true, Status = "Running",
                LastTick = status.LastCommittedTick,
                Note = "The room is already running on this host."
            };
        if (status.State == RoomRunState.Degraded)
            return new RecoverRoomReply
            {
                RoomId = request.RoomId, Recovered = true, Status = "Degraded",
                LastTick = status.LastCommittedTick,
                Note = "The room keeps committing on this host but its commits blow the tick " +
                       "budget (FIX-PERF-06); the slowness is observable, not silent."
            };
        if (status.State == RoomRunState.Preparing)
        {
            // FIX-REC-01: a deferred recovery's own held actor answers Ready — the pin and
            // the replay already landed; only the route barrier and the activation are left.
            // A lost-reply retry continues from here instead of re-recovering the room.
            if (status.HeldFromRecovery)
                return new RecoverRoomReply
                {
                    RoomId = request.RoomId, Recovered = true, Status = "Ready",
                    LastTick = status.LastCommittedTick,
                    Note = "The recovery prepared this room at its verified head; the loop " +
                           "starts when the control plane activates it after the route barrier."
                };
            return new RecoverRoomReply
            {
                RoomId = request.RoomId, Recovered = false, Status = "Preparing",
                LastTick = status.LastCommittedTick,
                Note = "The room is held for a common start (FIX-PERF-03) and has not begun " +
                       "ticking; recover it only after the start line releases it."
            };
        }
        if (status.State == RoomRunState.Paused)
            return new RecoverRoomReply
            {
                RoomId = request.RoomId, Recovered = true, Status = "Paused",
                LastTick = status.LastCommittedTick,
                Note = "The room is paused at a committed boundary on this host (FIX-SYNC-06); " +
                       "it is frozen, not running."
            };
        return new RecoverRoomReply
        {
            RoomId = request.RoomId, Recovered = false, Status = DescribeRunState(status.State),
            LastTick = status.LastCommittedTick,
            Note = status.StopReason is { Length: > 0 } stopReason
                ? $"The local actor stopped: {stopReason}"
                : "The local actor no longer ticks."
        };
    }

    /// <summary>The recovery-facing name of an actor's run state.</summary>
    internal static string DescribeRunState(RoomRunState state) => state switch
    {
        RoomRunState.Running => "Running",
        RoomRunState.Degraded => "Degraded",
        RoomRunState.Completed => "Finishing",
        RoomRunState.Paused => "Paused",
        RoomRunState.Stopped => "Stopped",
        RoomRunState.Preparing => "Preparing",
        _ => "Disposed"
    };
}

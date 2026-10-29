using System.Collections.Concurrent;
using System.Security.Cryptography;
using Chorus.Redesign.ExampleGame;
using Chorus.Redesign.Rpc;
using Google.Protobuf;
using Grpc.Core;

namespace Chorus.GameServer;

/// <summary>
/// Owns the room actors of one game-server process (architecture 7.2). PrepareRoom derives the
/// deterministic initial state from the module configuration, deposits the tick-0 snapshot on
/// the data plane, activates the round and starts the actor. The process holds no database
/// credentials — every persistent fact travels through the data-plane channel.
/// </summary>
public sealed class RoomRuntime : IRoomLifecycle, IAsyncDisposable
{
    private readonly Grpc.Net.Client.GrpcChannel? _dataChannel;
    private readonly RoundStore.RoundStoreClient _rounds;
    private readonly FrameStore.FrameStoreClient _frames;
    private readonly Settlement.SettlementClient _settlement;
    private readonly ConcurrentDictionary<string, RoomActor> _actors = new(StringComparer.Ordinal);
    // Per-room activation gates (FIX-ROOM-03): prepare and recovery reach the actor
    // dictionary only through their room's gate, so interleaved callers never both
    // decide the room is unknown and start a second loop.
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _activations = new(StringComparer.Ordinal);

    public RoomRuntime(Grpc.Net.Client.GrpcChannel dataChannel)
        : this(new RoundStore.RoundStoreClient(dataChannel), new FrameStore.FrameStoreClient(dataChannel),
            new Settlement.SettlementClient(dataChannel), dataChannel) { }

    /// <summary>Test seam: the data-plane clients scripted, the channel optional.</summary>
    internal RoomRuntime(RoundStore.RoundStoreClient rounds, FrameStore.FrameStoreClient frames,
        Settlement.SettlementClient settlement, Grpc.Net.Client.GrpcChannel? dataChannel = null)
    {
        _rounds = rounds;
        _frames = frames;
        _settlement = settlement;
        _dataChannel = dataChannel;
    }

    /// <summary>
    /// Prepares, activates and runs an admitted room. A second call for a room this process
    /// already knows answers duplicate without touching the room again — reruns of a finished
    /// or suspended room are a fresh room id until recovery lands (M2-4).
    /// </summary>
    public async Task<PrepareRoomReply> PrepareRoomAsync(PrepareRoomRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        Require(request.RoomId, "room_id"); Require(request.GameId, "game_id");
        Require(request.OwnerInstanceId, "owner_instance_id"); Require(request.OperationId, "operation_id");
        if (request.Epoch <= 0) throw new ArgumentException("The room epoch must be positive.");
        if (request.DurationTicks <= 0) throw new ArgumentException("The duration must be positive.");
        if (request.Participants.Count == 0)
            throw new ArgumentException("A round needs participants.");
        if (request.TickMs is < 1 or > 1000)
            throw new ArgumentException("The tick cadence is 1..1000 ms.");
        var bindings = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var participant in request.Participants)
        {
            Require(participant.BindingId, "participants.binding_id");
            Require(participant.ParticipantId, "participants.participant_id");
            Require(participant.Faction, "participants.faction");
            if (bindings.ContainsKey(participant.BindingId))
                throw new ArgumentException("Participant bindings must be distinct.");
            bindings[participant.BindingId] = participant.ParticipantId;
        }
        // FIX-ROOM-03: the check-activate-add sequence runs under the room's activation
        // gate, so a concurrent prepare or recovery for the same room waits and then sees
        // this call's outcome instead of racing past an empty dictionary.
        var activation = _activations.GetOrAdd(request.RoomId, static _ => new SemaphoreSlim(1, 1));
        await activation.WaitAsync(cancellationToken);
        try
        {
            if (_actors.ContainsKey(request.RoomId))
                return new PrepareRoomReply
                {
                    RoomId = request.RoomId, Duplicate = true, Status = "Prepared", StateVersion = 1
                };

            var manifest = new RedesignGameModuleManifest("example-factions", "1.0.0",
                request.TickMs > 0 ? request.TickMs : RedesignGameModuleManifest.Default.TickMs,
                RedesignGameModuleManifest.Default.SnapshotEveryTicks,
                RedesignGameModuleManifest.DeterministicReplay);
            var module = new ExampleGameModule(manifest);
            var initial = module.CreateInitialState(new GameStartConfig(request.DurationTicks,
                request.RandomSeed,
                request.Participants.Select(participant =>
                    new GameParticipant(participant.ParticipantId, participant.Faction)).ToList(),
                request.FailAfterTick));
            var payload = module.Serialize(initial);
            var hash = SHA256.HashData(payload);
            var configHash = Convert.ToHexString(hash).ToLowerInvariant();

            // Data-plane receipts under deterministic operation ids: a retry after a lost reply
            // replays the stored receipts instead of re-deriving anything.
            await _rounds.PrepareRoundAsync(new PrepareRoundRequest
            {
                RoomId = request.RoomId, OwnerInstanceId = request.OwnerInstanceId, Epoch = request.Epoch,
                GameId = request.GameId, SnapshotPayload = ByteString.CopyFrom(payload),
                SnapshotHash = ByteString.CopyFrom(hash), SnapshotSchemaVersion = initial.SchemaVersion,
                OperationId = request.OperationId + "/prepare"
            }, deadline: DateTime.UtcNow.AddSeconds(10), cancellationToken: cancellationToken);
            await _rounds.ActivateRoundAsync(new ActivateRoundRequest
            {
                RoomId = request.RoomId, OwnerInstanceId = request.OwnerInstanceId, Epoch = request.Epoch,
                GameId = request.GameId, OperationId = request.OperationId + "/activate"
            }, deadline: DateTime.UtcNow.AddSeconds(10), cancellationToken: cancellationToken);

            var actor = new RoomActor(this, _frames, module, request.GameId, request.RoomId,
                request.OwnerInstanceId, request.Epoch, configHash, initial, bindings);
            actor.LoopEnded = Retire;
            if (!_actors.TryAdd(request.RoomId, actor))
            {
                await actor.DisposeAsync();
                return new PrepareRoomReply
                {
                    RoomId = request.RoomId, Duplicate = true, Status = "Prepared", StateVersion = 1
                };
            }
            actor.Start();
            return new PrepareRoomReply
            {
                RoomId = request.RoomId, Duplicate = false, Status = "Running", StateVersion = 1
            };
        }
        finally
        {
            activation.Release();
        }
    }

    /// <summary>The actor of a room this host runs, or null — subscribers attach to it.</summary>
    internal RoomActor? FindRoom(string roomId) =>
        _actors.TryGetValue(roomId, out var actor) ? actor : null;

    /// <summary>
    /// Drops an actor whose loop ended without a committed result (FIX-ROOM-04): a stopped
    /// or disposed actor leaves the dictionary so the room id stops masquerading as running.
    /// The removal is identity-checked, so a delayed retirement never removes a newer actor
    /// that has since taken the room; a completed room keeps its actor for the result tail
    /// until release (FIX-END-14). Repeated retirement is a no-op.
    /// </summary>
    internal void Retire(RoomActor actor)
    {
        var status = actor.Status();
        if (status.State is RoomRunState.Running or RoomRunState.Completed) return;
        ((ICollection<KeyValuePair<string, RoomActor>>)_actors)
            .Remove(new KeyValuePair<string, RoomActor>(actor.RoomId, actor));
    }

    /// <summary>
    /// Recovers a room this process does not know (architecture 10.3): load the recovery plan,
    /// walk the newest usable snapshot forward through the committed frames — verifying every
    /// frame's state hash on the way — and resume ticking from the head. A corrupt or
    /// inconsistent newest snapshot falls back to the previous one; when no snapshot replays
    /// to the head the room is suspended visibly. A Finishing room recovers its tail without
    /// restarting battle ticks. Replay is read-only: frames and effects were committed once
    /// and are never re-triggered.
    /// </summary>
    public async Task<RecoverRoomReply> RecoverRoomAsync(RecoverRoomRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        Require(request.RoomId, "room_id"); Require(request.GameId, "game_id");
        Require(request.OwnerInstanceId, "owner_instance_id");
        if (request.Epoch <= 0) throw new ArgumentException("The room epoch must be positive.");
        if (request.TickMs is < 1 or > 1000)
            throw new ArgumentException("The tick cadence is 1..1000 ms.");
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
            return await RecoverUnknownRoomAsync(request, cancellationToken);
        }
        finally
        {
            activation.Release();
        }
    }

    /// <summary>Recovery of a room no local actor holds: load the plan, replay to the head
    /// and resume ticking. The caller holds the room's activation gate throughout.</summary>
    private async Task<RecoverRoomReply> RecoverUnknownRoomAsync(RecoverRoomRequest request,
        CancellationToken cancellationToken)
    {
        var plan = await _frames.LoadRecoveryAsync(new LoadRecoveryRequest
        {
            RoomId = request.RoomId, GameId = request.GameId
        }, deadline: DateTime.UtcNow.AddSeconds(10), cancellationToken: cancellationToken);
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
        // Finishing rooms recover their result and settlement tail only; battle ticks are
        // never restarted for a round that already froze its result. The settle call is
        // idempotent per grant: it applies whatever the manifest still owes (9.3).
        if (plan.Status == "Finishing")
        {
            await SettleRoomAsync(request.GameId, request.RoomId, cancellationToken);
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

        var manifest = new RedesignGameModuleManifest("example-factions", "1.0.0",
            request.TickMs, RedesignGameModuleManifest.Default.SnapshotEveryTicks,
            RedesignGameModuleManifest.DeterministicReplay);
        var module = new ExampleGameModule(manifest);
        var participantByBinding = plan.Participants
            .Where(participant => participant.BindingId.Length > 0 && participant.State != "Closed")
            .GroupBy(participant => participant.BindingId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().ParticipantId, StringComparer.Ordinal);
        var deliveryByEvent = plan.Deliveries
            .Where(delivery => delivery.EventId.Length > 0)
            .GroupBy(delivery => delivery.EventId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        foreach (var candidate in plan.Snapshots) // newest first
        {
            if (candidate.Tick > plan.LastTick) continue;
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
                        inputs.Add(new NormalizedInput(participant, delivery.PayloadJson));
                TickOutcome outcome;
                try { outcome = module.ApplyTick(state, frame.Tick, inputs); }
                catch (Exception) { consistent = false; break; }
                if (!module.ComputeStateHash(outcome.State).AsSpan().SequenceEqual(frame.StateHash.Span))
                {
                    consistent = false; break;
                }
                state = outcome.State;
                replayed++;
            }
            if (!consistent || state.Tick != plan.LastTick) continue;

            // The snapshot replays to the head with matching hashes: persist a fresh
            // store-verified snapshot at the head so the next recovery starts here.
            byte[] headPayload;
            try
            {
                headPayload = module.Serialize(state);
                await _frames.PutSnapshotAsync(new PutSnapshotRequest
                {
                    RoomId = request.RoomId, GameId = request.GameId,
                    OwnerInstanceId = request.OwnerInstanceId, Epoch = request.Epoch,
                    Tick = plan.LastTick, SnapshotPayload = ByteString.CopyFrom(headPayload),
                    SnapshotHash = ByteString.CopyFrom(SHA256.HashData(headPayload)),
                    SnapshotSchemaVersion = state.SchemaVersion,
                    OperationId = $"{request.OwnerInstanceId}/snapshot/{request.RoomId}/{plan.LastTick}"
                }, deadline: DateTime.UtcNow.AddSeconds(10), cancellationToken: cancellationToken);
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
            var configHash = plan.Frames.Count > 0
                ? plan.Frames.Max(frame => frame.Tick) == plan.LastTick
                    ? plan.Frames.First(frame => frame.Tick == plan.LastTick).ConfigHash
                    : Convert.ToHexString(SHA256.HashData(headPayload)).ToLowerInvariant()
                : Convert.ToHexString(SHA256.HashData(headPayload)).ToLowerInvariant();
            var actor = new RoomActor(this, _frames, module, request.GameId, request.RoomId,
                request.OwnerInstanceId, request.Epoch, configHash, state, participantByBinding,
                plan.LastTick, plan.StateVersion);
            actor.LoopEnded = Retire;
            if (!_actors.TryAdd(request.RoomId, actor))
            {
                await actor.DisposeAsync();
                return new RecoverRoomReply
                {
                    RoomId = request.RoomId, Recovered = true, Status = "Running",
                    Note = "The room is already running on this host."
                };
            }
            actor.Start();
            return new RecoverRoomReply
            {
                RoomId = request.RoomId, Recovered = true, Status = "Running",
                LastTick = plan.LastTick, SnapshotTick = candidate.Tick, ReplayedFrames = replayed,
                Note = $"Replayed {replayed} frames from the tick-{candidate.Tick} snapshot."
            };
        }

        // No snapshot survived replay: park the room visibly (architecture 10.3.6).
        await SuspendRoomAsync(request.GameId, request.RoomId, request.OwnerInstanceId, request.Epoch,
            "recovery failed: no consistent snapshot replays to the head", cancellationToken);
        return new RecoverRoomReply
        {
            RoomId = request.RoomId, Recovered = false, Status = "Suspended",
            Note = "No snapshot replays to the head with matching frame hashes; the room is suspended."
        };
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
        RoomRunState.Completed => "Finishing",
        RoomRunState.Stopped => "Stopped",
        _ => "Disposed"
    };

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

    public async ValueTask DisposeAsync()
    {
        foreach (var actor in _actors.Values) await actor.DisposeAsync();
        _actors.Clear();
        _dataChannel?.Dispose();
    }

    private static void Require(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("Value is required.", name);
    }
}

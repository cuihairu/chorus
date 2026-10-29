using System.Security.Cryptography;
using System.Text;
using Chorus.Redesign.ExampleGame;
using Chorus.Redesign.Rpc;
using Google.Protobuf;
using Grpc.Core;

namespace Chorus.GameServer;

/// <summary>
/// One running room (architecture 7, 8.2): a single loop that wakes on the module cadence,
/// reads the round inbox, applies one pure tick and commits it. The commit id is derived from
/// the tick, and the frame is held pending until the store returns a verdict: a reply lost
/// mid-flight resends the exact original request — the store replays its stored receipt —
/// and a committed or duplicate receipt advances the head exactly once. Module failures
/// isolate per room: three consecutive strikes suspend the room visibly and stop this actor
/// while other rooms keep ticking.
/// </summary>
internal sealed class RoomActor : IAsyncDisposable
{
    /// <summary>
    /// One tick held from the first send until a verdict (FIX-FRAME-01): the exact request
    /// to resend after an unknown outcome — never rebuilt from a re-read inbox — plus the
    /// candidate material one verdict publishes exactly once (FIX-FRAME-02).
    /// </summary>
    private sealed record PendingFrame(long Tick, long BaseVersion, ReadInboxReply Inbox,
        TickOutcome Outcome, CommitFrameRequest Request);

    private const int MaxInputsPerTick = 200;
    private const int MaxConsecutiveModuleFailures = 3;
    // Durable client commands ride the inbox on this platform; their dedup key is the
    // command id the subscriber correlates receipts against (architecture 6.3).
    private const string ClientCommandPlatform = "@chorus";

    private readonly IRoomLifecycle _runtime;
    private readonly FrameStore.FrameStoreClient _frames;
    private readonly ExampleGameModule _module;
    private readonly string _gameId;
    private readonly string _roomId;
    private readonly string _ownerInstanceId;
    private readonly long _epoch;
    private readonly string _configHash;
    private readonly IReadOnlyDictionary<string, string> _participantByBinding;
    private readonly CancellationTokenSource _cancellation = new();
    // Publishing and head reads share one gate: subscribers always see a coherent
    // (state, tick, version) triple of committed frames (architecture 8.3).
    private readonly object _publishGate = new();
    private readonly Dictionary<Guid, RoomSubscriber> _subscribers = new();
    private Task _run = Task.CompletedTask;
    private ExampleGameState _state;
    private long _lastTick;
    private long _stateVersion;
    private string? _finalResultJson;
    private int _consecutiveModuleFailures;
    private PendingFrame? _pending;
    private volatile bool _stopped;
    private volatile bool _finalCommitted;
    // Set under the publish gate when the loop exits: Subscribe must never register
    // against an actor whose publishing loop is gone.
    private bool _loopEnded;

    public RoomActor(IRoomLifecycle runtime, FrameStore.FrameStoreClient frames, ExampleGameModule module,
        string gameId, string roomId, string ownerInstanceId, long epoch, string configHash,
        ExampleGameState initialState, IReadOnlyDictionary<string, string> participantByBinding,
        long lastTick = 0, long stateVersion = 1)
    {
        _runtime = runtime;
        _frames = frames;
        _module = module;
        _gameId = gameId;
        _roomId = roomId;
        _ownerInstanceId = ownerInstanceId;
        _epoch = epoch;
        _configHash = configHash;
        _state = initialState;
        _participantByBinding = participantByBinding;
        // Fresh rooms start at the activation head (tick 0, state version 1); a recovered
        // room resumes from the head its frames already committed.
        _lastTick = lastTick;
        _stateVersion = stateVersion;
    }

    public void Start() => _run = Task.Run(() => RunAsync(_cancellation.Token));

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(_module.Manifest.TickMs));
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                if (_stopped) break;
                await TickOnceAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            // The loop must never die silently (architecture 10.3, fail-visible): park the
            // room and leave the evidence behind. The finally resolves every subscriber.
            _stopped = true;
            Console.Error.WriteLine($"room {_roomId} actor faulted: {error}");
            try
            {
                await _runtime.SuspendRoomAsync(_gameId, _roomId, _ownerInstanceId, _epoch,
                    $"the actor loop failed: {error.Message}", CancellationToken.None).ConfigureAwait(false);
            }
            catch { /* best-effort from a faulting host; the room stays inspectable */ }
        }
        finally
        {
            // However the loop ends, no stream may outlive it (architecture 8.3): a room
            // that stopped without committing its result — suspended, fenced or disposed —
            // answers every remaining subscriber with a resync instead of waiting for
            // frames that will never come. A finished room already delivered its result;
            // its subscribers only drain and end.
            lock (_publishGate)
            {
                SyncMessage? resync = null;
                if (!_finalCommitted)
                    resync = new SyncMessage { ResyncRequired = new ResyncRequiredMessage
                    {
                        RoomId = _roomId, CurrentEpoch = _epoch,
                        Reason = "the room stopped before committing a round result"
                    } };
                foreach (var subscriber in _subscribers.Values)
                {
                    if (resync is not null) subscriber.Offer(resync);
                    subscriber.Complete();
                }
                _subscribers.Clear();
                _loopEnded = true;
            }
        }
    }

    private async Task TickOnceAsync(CancellationToken cancellationToken)
    {
        // FIX-FRAME-01: a frame is built once and held until the store returns a verdict.
        // While it is unconfirmed the loop neither re-reads the inbox nor re-applies the
        // tick: a rebuild could consume different inputs and change the content under the
        // same commit id, which the store would reject as a conflict.
        if (_pending is null)
        {
            ReadInboxReply inbox;
            try
            {
                inbox = await _frames.ReadInboxAsync(
                    new ReadInboxRequest { RoomId = _roomId, GameId = _gameId, MaxEvents = MaxInputsPerTick },
                    deadline: Deadline(), cancellationToken: cancellationToken);
            }
            catch (RpcException)
            {
                return; // transient read failure: the next wake reads the same inbox
            }
            // Everything in the inbox is consumed this tick; only bindings this host mapped at
            // prepare time become module inputs, so a foreign delivery is acknowledged into the
            // frame record without inventing a participant.
            var inputs = new List<NormalizedInput>(inbox.Items.Count);
            foreach (var item in inbox.Items)
                if (item.BindingId is { Length: > 0 } binding
                    && _participantByBinding.TryGetValue(binding, out var participant))
                    inputs.Add(new NormalizedInput(participant, item.PayloadJson));

            var tick = _lastTick + 1;
            TickOutcome outcome;
            try
            {
                outcome = _module.ApplyTick(_state, tick, inputs);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                // A deterministic module failure repeats identically on retry; after three
                // strikes the room is parked visibly (AT13 isolation) and the actor stops.
                if (++_consecutiveModuleFailures >= MaxConsecutiveModuleFailures)
                {
                    _stopped = true;
                    await _runtime.SuspendRoomAsync(_gameId, _roomId, _ownerInstanceId, _epoch,
                        $"module failed {_consecutiveModuleFailures} consecutive ticks: {error.Message}",
                        cancellationToken).ConfigureAwait(false);
                }
                return;
            }
            _consecutiveModuleFailures = 0;
            _pending = new PendingFrame(tick, _stateVersion, inbox, outcome,
                BuildCommitRequest(tick, inbox, outcome));
        }

        var pendingFrame = _pending;
        FrameReceiptReply receipt;
        var retry = 0;
        while (true)
        {
            try
            {
                receipt = await _frames.CommitFrameAsync(pendingFrame.Request, deadline: Deadline(),
                    cancellationToken: cancellationToken);
                break;
            }
            catch (RpcException error) when (error.StatusCode
                is StatusCode.FailedPrecondition or StatusCode.InvalidArgument)
            {
                // The room left Running or the tick chain diverged; both need replay-based
                // recovery (M2-4). Park the room visibly instead of guessing forward. The
                // suspension itself may be fenced out too (an expired owner lease), so the
                // evidence line must not depend on it landing.
                _stopped = true;
                Console.Error.WriteLine(
                    $"room {_roomId} stopping: frame commit rejected at tick {pendingFrame.Tick}: {error.Status.Detail}");
                await _runtime.SuspendRoomAsync(_gameId, _roomId, _ownerInstanceId, _epoch,
                    $"frame commit rejected at tick {pendingFrame.Tick}: {error.Status.Detail}",
                    cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (RpcException)
            {
                // FIX-FRAME-03: the outcome is unknown — the frame may have committed and
                // only the reply was lost. Resend the exact request with bounded backoff
                // until a verdict arrives: a duplicate commit replays the stored receipt
                // (FIX-FRAME-02). Cancelling the loop is the only way out; suspending on a
                // maybe-committed frame would abandon a room that is still live.
                await Task.Delay(CommitRetryBackoff(retry++), cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        _pending = null;
        // Publish under the gate also advances the actor head there, so subscribers only
        // ever observe a coherent committed triple (8.3). A committed and a duplicate
        // receipt are the same verdict: the frame takes a single step forward.
        PublishCommittedFrame(pendingFrame.BaseVersion, pendingFrame.Inbox, pendingFrame.Outcome,
            receipt);
        if (pendingFrame.Outcome.Final)
        {
            // The room is Finishing; settle the committed manifest now. Settlement is
            // idempotent per grant, so a lost reply just means recovery retries it.
            _stopped = true;
            await _runtime.SettleRoomAsync(_gameId, _roomId, cancellationToken).ConfigureAwait(false);
        }
    }

    private CommitFrameRequest BuildCommitRequest(long tick, ReadInboxReply inbox, TickOutcome outcome)
    {
        var commit = new CommitFrameRequest
        {
            RoomId = _roomId, GameId = _gameId, OwnerInstanceId = _ownerInstanceId, Epoch = _epoch,
            CommitId = $"{_roomId}/tick-{tick}", PrevTick = _lastTick, PrevStateVersion = _stateVersion,
            StateHash = ByteString.CopyFrom(_module.ComputeStateHash(outcome.State)),
            ModuleVersion = _module.Manifest.ModuleVersion, ConfigHash = _configHash
        };
        commit.InputEventIds.AddRange(inbox.Items.Select(item => item.EventId));
        commit.Effects.AddRange(outcome.Effects.Select(effect => new FrameEffectProto
        {
            Kind = effect.Kind, PayloadJson = effect.PayloadJson
        }));
        if (tick % _module.Manifest.SnapshotEveryTicks == 0)
        {
            // FullCheckpoint on the module's cadence: the candidate state joins the frame
            // transaction (architecture 8.2) — a failed checkpoint rolls the tick back.
            var payload = _module.Serialize(outcome.State);
            commit.SnapshotPayload = ByteString.CopyFrom(payload);
            commit.SnapshotHash = ByteString.CopyFrom(SHA256.HashData(payload));
            commit.SnapshotSchemaVersion = outcome.State.SchemaVersion;
        }
        if (outcome.Final)
        {
            commit.Final = true;
            commit.ResultJson = outcome.ResultJson;
            commit.PlanHash = ByteString.CopyFrom(
                SHA256.HashData(Encoding.UTF8.GetBytes(outcome.ResultJson!)));
            // The immutable reward manifest (architecture 9.3): each participant earns the
            // final score of their faction. It derives from the module state — the factions
            // ride inside the snapshot — so a recovered actor commits the identical manifest.
            using var document = System.Text.Json.JsonDocument.Parse(outcome.ResultJson!);
            var scoreByFaction = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (var entry in document.RootElement.GetProperty("ranking").EnumerateArray())
                scoreByFaction[entry.GetProperty("faction").GetString()!] =
                    entry.GetProperty("score").GetInt64();
            foreach (var (player, faction) in outcome.State.FactionByParticipant
                         .OrderBy(pair => pair.Key, StringComparer.Ordinal))
                if (scoreByFaction.TryGetValue(faction, out var score) && score > 0)
                    commit.Grants.Add(new PlannedGrantProto
                    {
                        PlayerId = player, GrantKey = "round-score",
                        OperationId = $"{_roomId}/grant/{player}", AssetId = "points", Delta = score
                    });
        }
        return commit;
    }

    private static TimeSpan CommitRetryBackoff(int retry) =>
        TimeSpan.FromMilliseconds(Math.Min(250 * (1L << Math.Min(retry, 5)), 8_000));

    /// <summary>
    /// Fans the just-committed frame out to every subscriber (architecture 8.3): a coherent
    /// head, a delta with its base version, command receipts for consumed client commands.
    /// A subscriber whose queue overflowed is re-baselined with a snapshot of this committed
    /// state — never a delta with a base it never applied. The final frame publishes the
    /// frozen result and ends every stream. Never blocks the tick loop.
    /// </summary>
    private void PublishCommittedFrame(long baseVersion, ReadInboxReply inbox, TickOutcome outcome,
        FrameReceiptReply receipt)
    {
        lock (_publishGate)
        {
            _state = outcome.State;
            _lastTick = receipt.Tick;
            _stateVersion = receipt.StateVersion;
            if (_subscribers.Count == 0 && !outcome.Final) return;
            var delta = new SyncMessage { Delta = new DeltaMessage
            {
                RoomId = _roomId, Epoch = _epoch, Tick = receipt.Tick, StateVersion = receipt.StateVersion,
                BaseVersion = baseVersion, ModuleVersion = _module.Manifest.ModuleVersion,
                ConfigHash = _configHash,
                StateHash = ByteString.CopyFrom(_module.ComputeStateHash(outcome.State))
            } };
            delta.Delta.InputEventIds.AddRange(inbox.Items.Select(item => item.EventId));
            delta.Delta.Effects.AddRange(outcome.Effects.Select(effect => new FrameEffectProto
            {
                Kind = effect.Kind, PayloadJson = effect.PayloadJson
            }));
            delta.Delta.Commands.AddRange(inbox.Items
                .Where(item => item.Platform == ClientCommandPlatform)
                .Select(item => new CommandReceiptProto
                {
                    CommandId = item.SourceDedupKey, Sequence = item.Sequence
                }));
            SyncMessage? result = null;
            if (outcome.Final)
            {
                _finalResultJson = outcome.ResultJson;
                // Set inside the gate: a subscriber attaching after the result still gets
                // the finished-room answer instead of registering against a dead loop.
                _finalCommitted = true;
                result = new SyncMessage { RoundResult = new RoundResultMessage
                {
                    RoomId = _roomId, Epoch = _epoch, Tick = receipt.Tick,
                    StateVersion = receipt.StateVersion, ResultJson = outcome.ResultJson
                } };
            }
            foreach (var subscriber in _subscribers.Values)
            {
                if (subscriber.NeedsSnapshot)
                {
                    // The queue overflowed earlier: this committed state is the new
                    // baseline — the snapshot subsumes this frame's delta, and the frozen
                    // result never arrives over a gap.
                    subscriber.NeedsSnapshot = false;
                    subscriber.Offer(SnapshotOf(receipt.Tick, receipt.StateVersion, outcome.State));
                }
                else
                {
                    subscriber.Offer(delta);
                }
                if (result is not null)
                {
                    subscriber.Offer(result);
                    subscriber.Complete();
                }
            }
        }
    }

    private SyncMessage SnapshotOf(long tick, long stateVersion, ExampleGameState state)
    {
        var payload = _module.Serialize(state);
        return new SyncMessage { Snapshot = new SnapshotMessage
        {
            RoomId = _roomId, Epoch = _epoch, Tick = tick, StateVersion = stateVersion,
            ModuleVersion = _module.Manifest.ModuleVersion, ConfigHash = _configHash,
            StatePayload = ByteString.CopyFrom(payload), StateSchemaVersion = state.SchemaVersion
        } };
    }

    /// <summary>
    /// Attaches a subscriber (architecture 8.3). A baseline at the committed head continues
    /// with deltas alone; an older baseline first receives the head snapshot. A finished
    /// room answers its final snapshot and frozen result, then ends the stream.
    /// </summary>
    public RoomSubscriber Subscribe(long lastAppliedTick, long lastAppliedVersion)
    {
        var subscriber = new RoomSubscriber();
        lock (_publishGate)
        {
            if (_finalCommitted)
            {
                subscriber.Offer(SnapshotOf(_lastTick, _stateVersion, _state));
                subscriber.Offer(new SyncMessage { RoundResult = new RoundResultMessage
                {
                    RoomId = _roomId, Epoch = _epoch, Tick = _lastTick,
                    StateVersion = _stateVersion, ResultJson = _finalResultJson
                } });
                subscriber.Complete();
                return subscriber;
            }
            if (_loopEnded)
            {
                // The loop is gone without a committed result (suspended, fenced, disposed):
                // registering here would wait forever for frames that will never come.
                subscriber.Offer(new SyncMessage { ResyncRequired = new ResyncRequiredMessage
                {
                    RoomId = _roomId, CurrentEpoch = _epoch,
                    Reason = "the room stopped before committing a round result"
                } });
                subscriber.Complete();
                return subscriber;
            }
            if (lastAppliedTick != _lastTick || lastAppliedVersion != _stateVersion)
                subscriber.Offer(SnapshotOf(_lastTick, _stateVersion, _state));
            _subscribers.Add(Guid.NewGuid(), subscriber);
            return subscriber;
        }
    }

    /// <summary>Detaches a departed subscriber; its pump ends through cancellation.</summary>
    public void Detach(RoomSubscriber subscriber)
    {
        lock (_publishGate)
        {
            var key = _subscribers.FirstOrDefault(pair => pair.Value == subscriber).Key;
            if (key != Guid.Empty) _subscribers.Remove(key);
        }
    }

    /// <summary>The room epoch subscribers validate against (architecture 8.3).</summary>
    internal long Epoch => _epoch;

    /// <summary>True once the final frame committed: the room is Finishing on the data plane.</summary>
    public bool Completed => _finalCommitted;

    private static DateTime Deadline() => DateTime.UtcNow.AddSeconds(10);

    public async ValueTask DisposeAsync()
    {
        _stopped = true;
        _cancellation.Cancel();
        try { await _run.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        _cancellation.Dispose();
    }
}

/// <summary>
/// The actor's asks of its host runtime — parking a damaged room visibly and settling a
/// committed manifest. Kept to exactly the two calls the actor makes, so the commit-retry
/// contract can be exercised against a scripted runtime instead of a whole host.
/// </summary>
internal interface IRoomLifecycle
{
    Task SuspendRoomAsync(string gameId, string roomId, string ownerInstanceId, long epoch,
        string reason, CancellationToken cancellationToken = default);

    Task SettleRoomAsync(string gameId, string roomId, CancellationToken cancellationToken = default);
}

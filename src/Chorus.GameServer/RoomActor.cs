using System.Security.Cryptography;
using System.Text;
using Chorus.Redesign.ExampleGame;
using Chorus.Redesign.Rpc;
using Google.Protobuf;
using Grpc.Core;

namespace Chorus.GameServer;

/// <summary>The actor's lifecycle state (FIX-ROOM-01): what the loop is doing now. A
/// stopped actor carries its reason; nothing outside may infer liveness from timers.</summary>
internal enum RoomRunState
{
    /// <summary>Prepared and held: not yet released across the common start line
    /// (FIX-PERF-03). No loop runs and no frame has committed — the stall governor must
    /// not read a held fleet as frozen-Running.</summary>
    Preparing,
    /// <summary>The loop is awake and committing frames.</summary>
    Running,
    /// <summary>Every recent commit blew the tick budget (FIX-PERF-06): the room keeps
    /// committing — progress over silence — but no longer poses as Running.</summary>
    Degraded,
    /// <summary>The final frame committed and the manifest settled.</summary>
    Completed,
    /// <summary>The loop ended without a committed result: module failure, fence
    /// rejection or an actor fault. The reason says which.</summary>
    Stopped,
    /// <summary>The host disposed the actor while it was still live.</summary>
    Disposed
}

/// <summary>An inspectable snapshot of one room actor: its state, the epoch it runs at,
/// the last tick it committed, when the host saw that commit, and why it stopped
/// (FIX-ROOM-01, FIX-TAKE-06).</summary>
internal sealed record RoomActorStatus(RoomRunState State, long Epoch, long LastCommittedTick,
    string? StopReason, DateTimeOffset? LastCommittedAt = null);

/// <summary>The frozen outcome of a finished round (FIX-ROOM-05): the head the result
/// committed at, and the result itself — all that stays after the actor is released.</summary>
internal sealed record FinalRoundResult(long Epoch, long Tick, long StateVersion, string ResultJson);

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
    // FIX-PERF-06: an unknown commit outcome may not retry forever under a Running state —
    // after this many bounded resends the room is parked visibly for replay recovery.
    private const int MaxCommitRetries = 8;
    // FIX-PERF-06: a commit slower than TickMs × factor is over budget; this many
    // consecutive over-budget commits put the room in the observable Degraded state (it
    // keeps committing — progress over silence) and one on-budget commit clears it.
    private const int OverBudgetFactor = 10;
    private const int OverBudgetWindow = 10;
    // Durable client commands ride the inbox on this platform; their dedup key is the
    // command id the subscriber correlates receipts against (architecture 6.3).
    private const string ClientCommandPlatform = "@chorus";

    private readonly IRoomLifecycle _runtime;
    private readonly FrameStore.FrameStoreClient _frames;
    private readonly ExampleGameModule _module;
    private readonly RoomMetrics _metrics;
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
    private int _consecutiveOverBudget;
    private PendingFrame? _pending;
    private RoomRunState _runState = RoomRunState.Running;
    private string? _stopReason;
    private DateTimeOffset? _lastCommittedAt;
    // FIX-TAKE-07 test seam: the frozen-Running producer. Only the actor a prepare built
    // with the seam hangs — the actor a takeover recovers never carries it, so a migrated
    // room resumes from the pinned head instead of freezing again.
    private readonly int _hangAfterTick;
    private readonly int _hangMs;
    // FIX-PERF-06 test seams: an instant retry and a tighter budget keep the policy's unit
    // evidence fast; production keeps the constants above (0 = no override).
    private readonly TimeSpan? _commitRetryDelayOverride;
    private readonly int _overBudgetFactorOverride;
    private readonly int _overBudgetWindowOverride;
    private volatile bool _stopped;
    private volatile bool _disposed;
    private volatile bool _finalCommitted;
    // Set under the publish gate when the loop exits: Subscribe must never register
    // against an actor whose publishing loop is gone.
    private bool _loopEnded;

    public RoomActor(IRoomLifecycle runtime, FrameStore.FrameStoreClient frames, ExampleGameModule module,
        string gameId, string roomId, string ownerInstanceId, long epoch, string configHash,
        ExampleGameState initialState, IReadOnlyDictionary<string, string> participantByBinding,
        long lastTick = 0, long stateVersion = 1, int hangAfterTick = 0, int hangMs = 0,
        RoomMetrics? metrics = null, TimeSpan? commitRetryDelay = null,
        int overBudgetFactor = 0, int overBudgetWindow = 0)
    {
        _runtime = runtime;
        _frames = frames;
        _module = module;
        _metrics = metrics ?? RoomMetrics.Default;
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
        _hangAfterTick = hangAfterTick;
        _hangMs = hangMs;
        _commitRetryDelayOverride = commitRetryDelay;
        _overBudgetFactorOverride = overBudgetFactor;
        _overBudgetWindowOverride = overBudgetWindow;
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
            lock (_publishGate)
            {
                _runState = RoomRunState.Stopped;
                _stopReason = $"the actor loop failed: {error.Message}";
            }
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
            // FIX-ROOM-04: the runtime learns the loop ended here, outside the gate — it
            // retires an actor that will never publish again; a finished room keeps its
            // actor for the result tail.
            LoopEnded?.Invoke(this);
        }
    }

    private async Task TickOnceAsync(CancellationToken cancellationToken)
    {
        // FIX-PERF-01: the whole-tick clock wraps every phase and every early return
        // alike — a tick that ends on a transient read still pays its time to the record.
        var wholeStart = _metrics.Start();
        try
        {
            await TickOnceInnerAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _metrics.Record(_metrics.Whole, wholeStart);
        }
    }

    private async Task TickOnceInnerAsync(CancellationToken cancellationToken)
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
                var inboxStart = _metrics.Start();
                inbox = await _frames.ReadInboxAsync(
                    new ReadInboxRequest { RoomId = _roomId, GameId = _gameId, MaxEvents = MaxInputsPerTick },
                    deadline: Deadline(), cancellationToken: cancellationToken);
                _metrics.Record(_metrics.InboxPull, inboxStart);
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
            // The seam freezes the loop before the tick is applied or committed: the head
            // stays pinned while the process keeps reporting — exactly the stalled-room
            // evidence the control plane's governance acts on (FIX-TAKE-07).
            if (_hangAfterTick > 0 && tick >= _hangAfterTick && _hangMs > 0)
            {
                var hangUntil = Environment.TickCount64 + _hangMs;
                while (Environment.TickCount64 < hangUntil && !_cancellation.IsCancellationRequested)
                    Thread.Sleep(50);
            }
            TickOutcome outcome;
            try
            {
                var applyStart = _metrics.Start();
                outcome = _module.ApplyTick(_state, tick, inputs);
                _metrics.Record(_metrics.Apply, applyStart);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                // A deterministic module failure repeats identically on retry; after three
                // strikes the room is parked visibly (AT13 isolation) and the actor stops.
                if (++_consecutiveModuleFailures >= MaxConsecutiveModuleFailures)
                {
                    _stopped = true;
                    lock (_publishGate)
                    {
                        _runState = RoomRunState.Stopped;
                        _stopReason =
                            $"module failed {_consecutiveModuleFailures} consecutive ticks: {error.Message}";
                    }
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
        var commitStart = _metrics.Start();
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
                lock (_publishGate)
                {
                    _runState = RoomRunState.Stopped;
                    _stopReason =
                        $"frame commit rejected at tick {pendingFrame.Tick}: {error.Status.Detail}";
                }
                Console.Error.WriteLine(
                    $"room {_roomId} stopping: frame commit rejected at tick {pendingFrame.Tick}: {error.Status.Detail}");
                await _runtime.SuspendRoomAsync(_gameId, _roomId, _ownerInstanceId, _epoch,
                    $"frame commit rejected at tick {pendingFrame.Tick}: {error.Status.Detail}",
                    cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (RpcException error)
            {
                // FIX-FRAME-03: the outcome is unknown — the frame may have committed and
                // only the reply was lost. Resend the exact request with bounded backoff:
                // a duplicate commit replays the stored receipt (FIX-FRAME-02). FIX-PERF-06
                // bounds those resends — after MaxCommitRetries unknown outcomes the room is
                // parked visibly for replay recovery instead of retrying forever silent.
                // The pending frame was never published and its inputs were never consumed,
                // so recovery resumes from the store's truth either way.
                if (retry >= MaxCommitRetries)
                {
                    var reason =
                        $"the commit outcome stayed unknown after {MaxCommitRetries} retries " +
                        $"at tick {pendingFrame.Tick}: {error.Status.Detail}";
                    _stopped = true;
                    lock (_publishGate)
                    {
                        _runState = RoomRunState.Stopped;
                        _stopReason = reason;
                    }
                    Console.Error.WriteLine($"room {_roomId} stopping: {reason}");
                    await _runtime.SuspendRoomAsync(_gameId, _roomId, _ownerInstanceId, _epoch,
                        reason, cancellationToken).ConfigureAwait(false);
                    return;
                }
                await Task.Delay(_commitRetryDelayOverride ?? CommitRetryBackoff(retry), cancellationToken)
                    .ConfigureAwait(false);
                retry++;
                _metrics.CommitRetries.Add(1);
            }
        }
        _metrics.Record(_metrics.Commit, commitStart);
        AccountCommitBudget(commitStart, pendingFrame.Tick);
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
            var serializeStart = _metrics.Start();
            var payload = _module.Serialize(outcome.State);
            _metrics.Record(_metrics.Serialize, serializeStart);
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
    /// FIX-PERF-06: the sustained-slowness policy. A committed frame that took longer than
    /// TickMs × factor is over budget; overBudgetWindow consecutive over-budget commits put
    /// the room in the observable Degraded state — the loop keeps committing (progress over
    /// silence), the recovery face names it, and no frame or input is touched here. One
    /// on-budget commit clears the streak and the state.
    /// </summary>
    private void AccountCommitBudget(long commitStart, long tick)
    {
        var factor = _overBudgetFactorOverride > 0 ? _overBudgetFactorOverride : OverBudgetFactor;
        var window = _overBudgetWindowOverride > 0 ? _overBudgetWindowOverride : OverBudgetWindow;
        if (_metrics.Elapsed(commitStart) > _module.Manifest.TickMs * (double)factor)
        {
            if (++_consecutiveOverBudget < window) return;
            lock (_publishGate)
            {
                if (_runState != RoomRunState.Running) return;
                _runState = RoomRunState.Degraded;
            }
            Console.Error.WriteLine(
                $"room {_roomId} degraded at tick {tick}: the last {window} commits each took " +
                $"longer than the {_module.Manifest.TickMs * factor}ms budget " +
                $"(tick interval {_module.Manifest.TickMs} ms)");
            return;
        }
        _consecutiveOverBudget = 0;
        lock (_publishGate)
        {
            if (_runState == RoomRunState.Degraded) _runState = RoomRunState.Running;
        }
    }

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
            // The wall-clock evidence the progress report carries (FIX-TAKE-06): the host
            // saw this commit land; the control plane keeps its own database-time tracking.
            _lastCommittedAt = DateTimeOffset.UtcNow;
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
                _runState = RoomRunState.Completed;
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
        var serializeStart = _metrics.Start();
        var payload = _module.Serialize(state);
        _metrics.Record(_metrics.Serialize, serializeStart);
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

    /// <summary>The room this actor runs — the runtime's retirement key (FIX-ROOM-04).</summary>
    internal string RoomId => _roomId;

    /// <summary>Invoked once when the loop has fully ended, outside the publish gate.
    /// The runtime retires actors that stopped without a result; finished rooms keep
    /// theirs for the result tail.</summary>
    internal Action<RoomActor>? LoopEnded { get; set; }

    /// <summary>True once the final frame committed: the room is Finishing on the data plane.</summary>
    public bool Completed => _finalCommitted;

    /// <summary>The frozen result of a finished room (FIX-ROOM-05): what a bounded result
    /// cache keeps after the actor itself is released — or null while the round runs.</summary>
    internal FinalRoundResult? FinalResult()
    {
        lock (_publishGate) return _finalCommitted
            ? new FinalRoundResult(_epoch, _lastTick, _stateVersion, _finalResultJson!)
            : null;
    }

    /// <summary>The actor's lifecycle snapshot (FIX-ROOM-01): state, epoch, committed head
    /// and — for a room that stopped without a result — the recorded reason.</summary>
    public RoomActorStatus Status()
    {
        lock (_publishGate) return new RoomActorStatus(_runState, _epoch, _lastTick, _stopReason,
            _lastCommittedAt);
    }

    /// <summary>The activation fence this actor was built under (FIX-PERF-03 release check;
    /// the epoch itself is the subscriber-facing property above).</summary>
    internal string GameId => _gameId;
    internal string OwnerInstanceId => _ownerInstanceId;

    /// <summary>A prepared-but-unreleased room reports Preparing (FIX-PERF-03).</summary>
    internal void HoldForCommonStart()
    {
        lock (_publishGate) _runState = RoomRunState.Preparing;
    }

    /// <summary>The release across the start line: back to Running before the loop starts.</summary>
    internal void ReleaseFromHold()
    {
        lock (_publishGate)
        {
            if (_runState == RoomRunState.Preparing) _runState = RoomRunState.Running;
        }
    }

    private static DateTime Deadline() => DateTime.UtcNow.AddSeconds(10);

    public async ValueTask DisposeAsync()
    {
        // Idempotent: the second dispose (an explicit call after `await using`, or a
        // racing teardown) must not throw on the already-disposed token source.
        if (_disposed) return;
        _disposed = true;
        _stopped = true;
        lock (_publishGate)
        {
            // A finished room keeps its Completed state through teardown; a live room the
            // host is shutting down records that it was disposed, not that it stopped —
            // a degraded room is still live, so it records Disposed too.
            if (_runState is RoomRunState.Running or RoomRunState.Degraded)
                _runState = RoomRunState.Disposed;
        }
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

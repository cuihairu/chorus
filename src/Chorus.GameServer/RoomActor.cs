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
/// the tick, so a reply lost mid-flight replays the stored receipt on the next wake instead of
/// double-committing. Module failures isolate per room: three consecutive strikes suspend the
/// room visibly and stop this actor while other rooms keep ticking.
/// </summary>
internal sealed class RoomActor : IAsyncDisposable
{
    private const int MaxInputsPerTick = 200;
    private const int MaxConsecutiveModuleFailures = 3;

    private readonly RoomRuntime _runtime;
    private readonly FrameStore.FrameStoreClient _frames;
    private readonly ExampleGameModule _module;
    private readonly string _gameId;
    private readonly string _roomId;
    private readonly string _ownerInstanceId;
    private readonly long _epoch;
    private readonly string _configHash;
    private readonly IReadOnlyDictionary<string, string> _participantByBinding;
    private readonly CancellationTokenSource _cancellation = new();
    private Task _run = Task.CompletedTask;
    private ExampleGameState _state;
    private long _lastTick;
    private long _stateVersion;
    private int _consecutiveModuleFailures;
    private volatile bool _stopped;
    private volatile bool _finalCommitted;

    public RoomActor(RoomRuntime runtime, FrameStore.FrameStoreClient frames, ExampleGameModule module,
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
                if (_stopped) return;
                await TickOnceAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task TickOnceAsync(CancellationToken cancellationToken)
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
        }

        FrameReceiptReply receipt;
        try
        {
            receipt = await _frames.CommitFrameAsync(commit, deadline: Deadline(),
                cancellationToken: cancellationToken);
        }
        catch (RpcException error) when (error.StatusCode
            is StatusCode.FailedPrecondition or StatusCode.InvalidArgument)
        {
            // The room left Running or the tick chain diverged; both need replay-based
            // recovery (M2-4). Park the room visibly instead of guessing forward.
            _stopped = true;
            await _runtime.SuspendRoomAsync(_gameId, _roomId, _ownerInstanceId, _epoch,
                $"frame commit rejected at tick {tick}: {error.Status.Detail}",
                cancellationToken).ConfigureAwait(false);
            return;
        }
        catch (RpcException)
        {
            return; // outcome unknown: the same commit id replays its receipt next wake (8.2)
        }
        _state = outcome.State;
        _lastTick = receipt.Tick;
        _stateVersion = receipt.StateVersion;
        if (outcome.Final)
        {
            // The room is Finishing; settlement is M2-5. Recovery asks must answer the
            // tail, never restart battle ticks.
            _finalCommitted = true;
            _stopped = true;
        }
    }

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

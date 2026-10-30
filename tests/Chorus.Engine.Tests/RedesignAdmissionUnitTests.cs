using Chorus.GameServer;
using Chorus.ExampleGame;
using Chorus.Rpc;
using Google.Protobuf;
using Grpc.Core;
using Xunit;

namespace Chorus.Engine.Tests;

/// <summary>
/// The single GameServer admission budget (FIX-PERF-07) as unit evidence: a saturated host
/// refuses a new allocation without touching the rooms it already runs; concurrent prepares
/// of different rooms never exceed the cap; a failed creation returns its slot; and the
/// measured-load dimension refuses new allocations while the host demonstrably overloads
/// (rooms observably Degraded, FIX-PERF-06). Invalid configuration fails closed.
/// </summary>
public sealed class RedesignAdmissionUnitTests
{
    [Fact]
    public async Task ASaturatedHostRefusesNewRoomsButExistingRoomsKeepRunning()
    {
        var rounds = new ScriptedRoundStore();
        var frames = new ScriptedFrameStore();
        await using var runtime = new RoomRuntime(rounds, frames, new ScriptedSettlement(),
            maxActiveRooms: 1);

        var first = await runtime.PrepareRoomAsync(Prepare("room-sat-a", "op-1"));
        Assert.False(first.Duplicate);
        Assert.True(await WaitForAsync(() => Task.FromResult(frames.CommitCount >= 2)),
            "The admitted room never committed a frame.");

        // The saturated host refuses the second room as a capacity refusal — never a reply a
        // caller could mistake for a duplicate — while the running room keeps ticking.
        var refusal = await Assert.ThrowsAsync<RoomAdmissionException>(
            () => runtime.PrepareRoomAsync(Prepare("room-sat-b", "op-2")));
        Assert.Contains("admission cap", refusal.Message);
        Assert.True(await WaitForAsync(() => Task.FromResult(frames.CommitCount >= 4)),
            "The running room stopped committing under a refused admission.");
        Assert.NotNull(runtime.FindRoom("room-sat-a"));
        Assert.Null(runtime.FindRoom("room-sat-b"));
        // One room held, one admission slot consumed.
        Assert.Single(runtime.RoomStatuses());
    }

    [Fact]
    public async Task ConcurrentPreparesNeverExceedTheCap()
    {
        var rounds = new ScriptedRoundStore();
        var frames = new ScriptedFrameStore();
        await using var runtime = new RoomRuntime(rounds, frames, new ScriptedSettlement(),
            maxActiveRooms: 2);

        var outcomes = await Task.WhenAll(Enumerable.Range(1, 5)
            .Select(index => TryPrepareAsync(runtime, $"room-conc-{index}", $"op-{index}")));

        // The atomic reservation is the cap: five concurrent prepares admit exactly two and
        // refuse three — a bare dictionary count would have raced all five through.
        Assert.Equal(2, outcomes.Count(outcome => outcome == "admitted"));
        Assert.Equal(3, outcomes.Count(outcome => outcome == "refused"));
        Assert.Equal(2, runtime.RoomStatuses().Count);
        Assert.Equal(2, rounds.PrepareCalls);
        Assert.Equal(2, rounds.ActivateCalls);
    }

    [Fact]
    public async Task AFailedCreationReleasesItsSlotForTheNextLegalRoom()
    {
        var rounds = new ScriptedRoundStore
        {
            Prepare = _ => Task.FromException<RoundAdmissionReply>(
                new RpcException(new Status(StatusCode.Unavailable, "the store is away")))
        };
        var frames = new ScriptedFrameStore();
        await using var runtime = new RoomRuntime(rounds, frames, new ScriptedSettlement(),
            maxActiveRooms: 1);

        // The one slot is taken by a prepare whose data-plane work then fails; the failed
        // creation must return the slot, or the next room would be refused at cap.
        await Assert.ThrowsAsync<RpcException>(
            () => runtime.PrepareRoomAsync(Prepare("room-fail-a", "op-1")));
        rounds.Prepare = null; // the store is back
        var retry = await runtime.PrepareRoomAsync(Prepare("room-fail-b", "op-2"));
        Assert.False(retry.Duplicate);
        Assert.True(await WaitForAsync(() => Task.FromResult(frames.CommitCount >= 1)),
            "The room admitted after a failed creation never committed a frame.");
        Assert.NotNull(runtime.FindRoom("room-fail-b"));
    }

    [Fact]
    public async Task DegradedRoomsAtTheLoadBudgetRefuseNewAllocationsUntilTheyDrain()
    {
        var rounds = new ScriptedRoundStore();
        var frames = new ScriptedFrameStore();
        // Active-room cap high, so the refusal is the measured-load dimension: the host lets
        // one room be observably overloaded (Degraded, FIX-PERF-06) and then refuses until it
        // drains. The seams make the degrade/recover evidence fast; production holds 10x10.
        await using var runtime = new RoomRuntime(rounds, frames, new ScriptedSettlement(),
            maxActiveRooms: 10, maxDegradedRooms: 1, overBudgetFactor: 4, overBudgetWindow: 3);
        frames.DefaultCommit = request => SlowCommit(130, request);

        var first = await runtime.PrepareRoomAsync(Prepare("room-load-a", "op-1"));
        Assert.False(first.Duplicate);
        Assert.True(await WaitForAsync(() => Task.FromResult(
                frames.CommitCount >= 3 && runtime.FindRoom("room-load-a")!.Status().State == RoomRunState.Degraded)),
            "The slow room never became observably Degraded.");

        // The measured load is at its budget: a new allocation is refused, even though the
        // active-room cap would have room for it.
        var refusal = await Assert.ThrowsAsync<RoomAdmissionException>(
            () => runtime.PrepareRoomAsync(Prepare("room-load-b", "op-2")));
        Assert.Contains("load", refusal.Message);

        // The host drains: one on-budget commit clears the streak and the state, and the
        // load budget admits the next room.
        frames.DefaultCommit = request => Task.FromResult(Receipt(request));
        Assert.True(await WaitForAsync(() => Task.FromResult(
                runtime.FindRoom("room-load-a")!.Status().State == RoomRunState.Running)),
            "The degraded room never drained back to Running.");
        var admitted = await runtime.PrepareRoomAsync(Prepare("room-load-b", "op-3"));
        Assert.False(admitted.Duplicate);
        Assert.NotNull(runtime.FindRoom("room-load-b"));
    }

    [Fact]
    public void InvalidAdmissionConfigurationFailsClosed()
    {
        // FIX-PERF-07: a configured budget is never silently disabled. A non-positive cap is
        // a configuration error — the host must refuse to start, not admit unbounded rooms.
        Assert.Throws<ArgumentOutOfRangeException>(() => new RoomAdmissionBudget(0, null));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RoomAdmissionBudget(null, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RoomAdmissionBudget(5, 0));
        // Absent budgets are genuinely unlimited; that is the explicit, not accidental, state.
        Assert.NotNull(new RoomAdmissionBudget(null, null));
    }

    [Fact]
    public void TheLoadBudgetIsCheckedBeforeTheActiveRoomCap()
    {
        // A host already at its measured-load budget refuses on load, never admitting past it
        // just because the room cap still has headroom — the load refusal takes no slot.
        var budget = new RoomAdmissionBudget(maxActiveRooms: 10, maxDegradedRooms: 1);
        Assert.Equal(AdmissionOutcome.LoadBudget, budget.TryReserve(degradedRooms: 1));
        Assert.Equal(0, budget.ActiveRooms);
        // Under the load budget, the active-room cap still binds and never frees a refused slot.
        var tight = new RoomAdmissionBudget(maxActiveRooms: 1, maxDegradedRooms: null);
        Assert.Equal(AdmissionOutcome.Admitted, tight.TryReserve(degradedRooms: 0));
        Assert.Equal(AdmissionOutcome.ActiveRoomCap, tight.TryReserve(degradedRooms: 0));
        Assert.Equal(1, tight.ActiveRooms);
        tight.Release();
        Assert.Equal(0, tight.ActiveRooms);
    }

    [Fact]
    public void WouldRefuseMirrorsTheBudgetWithoutConsumingASlot()
    {
        // FIX-SCHED-02's report surface: the predicate answers what TryReserve would do,
        // over the same dimensions, while taking nothing — a self-report must never move
        // capacity by asking about it.
        var budget = new RoomAdmissionBudget(maxActiveRooms: 2, maxDegradedRooms: null);
        for (var index = 0; index < 5; index++)
            Assert.False(budget.WouldRefuse(degradedRooms: 0));
        Assert.Equal(0, budget.ActiveRooms);
        Assert.Equal(AdmissionOutcome.Admitted, budget.TryReserve(degradedRooms: 0));
        Assert.Equal(AdmissionOutcome.Admitted, budget.TryReserve(degradedRooms: 0));
        Assert.True(budget.WouldRefuse(degradedRooms: 0));
        Assert.Equal(AdmissionOutcome.ActiveRoomCap, budget.TryReserve(degradedRooms: 0));
        Assert.Equal(2, budget.ActiveRooms); // the predicate's questions left the cap intact
        budget.Release();
        Assert.False(budget.WouldRefuse(degradedRooms: 0));
        // The load dimension shows through the predicate at zero active rooms.
        var loaded = new RoomAdmissionBudget(maxActiveRooms: 10, maxDegradedRooms: 1);
        Assert.True(loaded.WouldRefuse(degradedRooms: 1));
        Assert.Equal(0, loaded.ActiveRooms);
        // No budget configured: the host never reports itself full.
        Assert.False(new RoomAdmissionBudget(null, null).WouldRefuse(degradedRooms: 999));
    }

    [Fact]
    public async Task TheSelfReportedAdmissionStateFollowsTheHeldSlots()
    {
        // The word the process report publishes (FIX-SCHED-02): Accepting while the host
        // has headroom, Full the moment its own budget would refuse — the scheduler filters
        // on this instead of offering rooms a saturated host must refuse.
        var rounds = new ScriptedRoundStore();
        var frames = new ScriptedFrameStore();
        await using var runtime = new RoomRuntime(rounds, frames, new ScriptedSettlement(),
            maxActiveRooms: 1);
        Assert.Equal(ProcessAdmission.Accepting, runtime.AdmissionState);
        var first = await runtime.PrepareRoomAsync(Prepare("room-report-a", "op-report-1"));
        Assert.False(first.Duplicate);
        Assert.Equal(ProcessAdmission.Full, runtime.AdmissionState);
        await Assert.ThrowsAsync<RoomAdmissionException>(
            () => runtime.PrepareRoomAsync(Prepare("room-report-b", "op-report-2")));
        Assert.Equal(ProcessAdmission.Full, runtime.AdmissionState);
    }

    private static async Task<string> TryPrepareAsync(RoomRuntime runtime, string roomId, string operationId)
    {
        try
        {
            await runtime.PrepareRoomAsync(Prepare(roomId, operationId));
            return "admitted";
        }
        catch (RoomAdmissionException)
        {
            return "refused";
        }
    }

    private static PrepareRoomRequest Prepare(string roomId, string operationId) => new()
    {
        RoomId = roomId, GameId = "game-a", OwnerInstanceId = "game-1", Epoch = 1,
        OperationId = operationId, DurationTicks = 200, RandomSeed = 777, TickMs = 25,
        Participants =
        {
            new GameParticipantProto { ParticipantId = "host-adm", BindingId = "binding-host-adm", Faction = GameStartConfig.RedFaction },
            new GameParticipantProto { ParticipantId = "guest-adm", BindingId = "binding-guest-adm", Faction = GameStartConfig.BlueFaction }
        }
    };

    private static Task<FrameReceiptReply> SlowCommit(int ms, CommitFrameRequest request) =>
        Task.Delay(ms).ContinueWith(_ => Receipt(request));

    private static FrameReceiptReply Receipt(CommitFrameRequest request) => new()
    {
        CommitId = request.CommitId, RoomId = request.RoomId,
        Tick = request.PrevTick + 1, StateVersion = request.PrevStateVersion + 1
    };

    private static async Task<bool> WaitForAsync(Func<Task<bool>> probe)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            if (await probe()) return true;
            await Task.Delay(20);
        }
        return await probe();
    }

    /// <summary>The round store with a scriptable prepare and counted activations.</summary>
    private sealed class ScriptedRoundStore : RoundStore.RoundStoreClient
    {
        private readonly object _gate = new();
        private int _prepareCalls;
        private int _activateCalls;

        public Func<PrepareRoundRequest, Task<RoundAdmissionReply>>? Prepare { get; set; }

        public int PrepareCalls { get { lock (_gate) return _prepareCalls; } }
        public int ActivateCalls { get { lock (_gate) return _activateCalls; } }

        public override AsyncUnaryCall<RoundAdmissionReply> PrepareRoundAsync(PrepareRoundRequest request,
            Metadata? headers = null, DateTime? deadline = null,
            CancellationToken cancellationToken = default)
        {
            lock (_gate) _prepareCalls++;
            var answer = Prepare ?? (_ => Task.FromResult(new RoundAdmissionReply
            {
                RoomId = request.RoomId, Status = "Preparing"
            }));
            return new AsyncUnaryCall<RoundAdmissionReply>(answer(request),
                Task.FromResult(new Metadata()), () => Status.DefaultSuccess,
                () => new Metadata(), () => { });
        }

        public override AsyncUnaryCall<RoundAdmissionReply> ActivateRoundAsync(ActivateRoundRequest request,
            Metadata? headers = null, DateTime? deadline = null,
            CancellationToken cancellationToken = default)
        {
            lock (_gate) _activateCalls++;
            return new AsyncUnaryCall<RoundAdmissionReply>(
                Task.FromResult(new RoundAdmissionReply { RoomId = request.RoomId, Status = "Running" }),
                Task.FromResult(new Metadata()), () => Status.DefaultSuccess,
                () => new Metadata(), () => { });
        }
    }

    /// <summary>The frame store: a scriptable commit reply, plain successes otherwise.</summary>
    private sealed class ScriptedFrameStore : FrameStore.FrameStoreClient
    {
        private readonly object _gate = new();
        private int _commits;

        public Func<CommitFrameRequest, Task<FrameReceiptReply>>? DefaultCommit { get; set; }

        public int CommitCount { get { lock (_gate) return _commits; } }

        public override AsyncUnaryCall<ReadInboxReply> ReadInboxAsync(ReadInboxRequest request,
            Metadata? headers = null, DateTime? deadline = null,
            CancellationToken cancellationToken = default) =>
            new(Task.FromResult(new ReadInboxReply()), Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess, () => new Metadata(), () => { });

        public override AsyncUnaryCall<FrameReceiptReply> CommitFrameAsync(CommitFrameRequest request,
            Metadata? headers = null, DateTime? deadline = null,
            CancellationToken cancellationToken = default)
        {
            lock (_gate) _commits++;
            var answer = DefaultCommit ?? (commit => Task.FromResult(Receipt(commit)));
            return new AsyncUnaryCall<FrameReceiptReply>(answer(request),
                Task.FromResult(new Metadata()), () => Status.DefaultSuccess,
                () => new Metadata(), () => { });
        }
    }

    /// <summary>Settlement always succeeds; these rooms never finish inside a test.</summary>
    private sealed class ScriptedSettlement : Settlement.SettlementClient
    {
        public override AsyncUnaryCall<CommitResultReply> CommitResultAsync(CommitResultRequest request,
            Metadata? headers = null, DateTime? deadline = null,
            CancellationToken cancellationToken = default) =>
            new(Task.FromResult(new CommitResultReply()), Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess, () => new Metadata(), () => { });
    }
}

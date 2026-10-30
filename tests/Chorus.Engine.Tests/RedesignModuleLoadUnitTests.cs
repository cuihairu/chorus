using Chorus.ExampleGame;
using Chorus.GameServer;
using Chorus.Rpc;
using Google.Protobuf;
using Grpc.Core;
using Xunit;

namespace Chorus.Engine.Tests;

/// <summary>
/// FIX-MODULE-02, the prepare leg: a room is loaded through the package registry — the
/// package the request names decides which module runs, an empty hash is the cluster
/// default the coordinator pins, and a hash this host does not ship is refused by name
/// before any capacity or data-plane work, with no fallback to the default example
/// module (the same room still prepares cleanly once it names a shipped package).
/// </summary>
public sealed class RedesignModuleLoadUnitTests
{
    [Fact]
    public async Task ARoomUnderTheDuelPackageRunsTheDuelModule()
    {
        var rounds = new ScriptedRoundStore();
        var frames = new ScriptedFrameStore { Inbox = GiftEveryTick() };
        await using var runtime = new RoomRuntime(rounds, frames, new ScriptedSettlement());

        var prepared = await runtime.PrepareRoomAsync(Room(packageHash: "pkg-duel-1"));
        Assert.Equal("Running", prepared.Status);

        // The duel module's own facts and arithmetic, not the default module's: the
        // first commit carries the duel version and the state hash only the duel's
        // rules produce (a gift scores nothing there — the example module would have
        // moved red by 7 and hashed differently).
        Assert.True(await WaitForAsync(() => Task.FromResult(frames.Commits.Count >= 1)),
            "The duel room never committed a frame.");
        var first = frames.Commits[0];
        Assert.Equal("0.9.0", first.ModuleVersion);
        Assert.Equal(
            Convert.ToHexString(DuelHashAtTickOne(frames)),
            Convert.ToHexString(first.StateHash.ToByteArray()));
        Assert.Empty(first.Effects); // the gift is noise to a duel
    }

    [Fact]
    public async Task ARoomWithoutAPackageRunsTheClusterDefault()
    {
        var frames = new ScriptedFrameStore { Inbox = GiftEveryTick() };
        await using var runtime = new RoomRuntime(new ScriptedRoundStore(), frames,
            new ScriptedSettlement());

        var prepared = await runtime.PrepareRoomAsync(Room(packageHash: ""));
        Assert.Equal("Running", prepared.Status);

        Assert.True(await WaitForAsync(() => Task.FromResult(frames.Commits.Count >= 1)),
            "The default room never committed a frame.");
        Assert.Equal(GameModuleIdentity.ModuleVersion, frames.Commits[0].ModuleVersion);
        Assert.NotEmpty(frames.Commits[0].Effects); // the same gift scores under pkg-1
    }

    [Fact]
    public async Task ARoomUnderAnUnshippedPackageIsRefusedWithoutFallback()
    {
        var rounds = new ScriptedRoundStore();
        var frames = new ScriptedFrameStore();
        await using var runtime = new RoomRuntime(rounds, frames, new ScriptedSettlement());

        var error = await Assert.ThrowsAsync<ModulePackageException>(
            () => runtime.PrepareRoomAsync(Room(packageHash: "pkg-gone", operationId: "op-1")));
        Assert.Contains("pkg-gone", error.Message, StringComparison.Ordinal);
        Assert.Contains("pkg-1", error.Message, StringComparison.Ordinal);
        Assert.Contains("pkg-duel-1", error.Message, StringComparison.Ordinal);
        // Refused before any work: nothing reached the data plane, no actor exists.
        Assert.Equal(0, rounds.PrepareCalls);
        Assert.Equal(0, frames.CommitCount);
        Assert.Null(runtime.FindRoom("room-load"));

        // No fallback happened either: the same room prepares cleanly once it names a
        // shipped package — the refusal left no trace that could turn this duplicate.
        var retry = await runtime.PrepareRoomAsync(Room(packageHash: "pkg-1", operationId: "op-2"));
        Assert.False(retry.Duplicate);
        Assert.Equal("Running", retry.Status);
        Assert.Equal(1, rounds.PrepareCalls);
    }

    /// <summary>The expected duel hash of tick 1: the duel module over the room's initial
    /// state with the scripted gift input — what the registry-loaded module commits.</summary>
    private static byte[] DuelHashAtTickOne(ScriptedFrameStore frames)
    {
        var duel = RedesignModuleCatalog.Duel.Load(RedesignModuleCatalog.Duel.ManifestAt(25));
        var initial = duel.CreateInitialState(StartConfig());
        var outcome = duel.ApplyTick(initial, 1,
            [new NormalizedInput("host-load", frames.Inbox!().Items[0].PayloadJson)]);
        return duel.ComputeStateHash(outcome.State);
    }

    private static GameStartConfig StartConfig() => new(200, 777,
        [new GameParticipant("host-load", GameStartConfig.RedFaction),
         new GameParticipant("guest-load", GameStartConfig.BlueFaction)], 0);

    /// <summary>One gift for the host binding on every inbox read: a steady input the two
    /// modules score differently.</summary>
    private static Func<ReadInboxReply> GiftEveryTick() => () => new ReadInboxReply
    {
        Items = { new InboxItemProto
        {
            EventId = "evt-gift", BindingId = "binding-host-load",
            PayloadJson = """{"kind":"gift","amount":7}"""
        } }
    };

    private static PrepareRoomRequest Room(string packageHash, string operationId = "op-load") => new()
    {
        RoomId = "room-load", GameId = "game-a", OwnerInstanceId = "game-1", Epoch = 1,
        OperationId = operationId, DurationTicks = 200, RandomSeed = 777, TickMs = 25,
        PackageHash = packageHash,
        Participants =
        {
            new GameParticipantProto
            {
                ParticipantId = "host-load", BindingId = "binding-host-load",
                Faction = GameStartConfig.RedFaction
            },
            new GameParticipantProto
            {
                ParticipantId = "guest-load", BindingId = "binding-guest-load",
                Faction = GameStartConfig.BlueFaction
            }
        }
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

    /// <summary>The round store with counted prepares; plain successes otherwise.</summary>
    private sealed class ScriptedRoundStore : RoundStore.RoundStoreClient
    {
        private readonly object _gate = new();
        private int _prepareCalls;

        public int PrepareCalls { get { lock (_gate) return _prepareCalls; } }

        public override AsyncUnaryCall<RoundAdmissionReply> PrepareRoundAsync(PrepareRoundRequest request,
            Metadata? headers = null, DateTime? deadline = null,
            CancellationToken cancellationToken = default)
        {
            lock (_gate) _prepareCalls++;
            return new AsyncUnaryCall<RoundAdmissionReply>(
                Task.FromResult(new RoundAdmissionReply
                {
                    RoomId = request.RoomId, Status = "Preparing"
                }),
                Task.FromResult(new Metadata()), () => Status.DefaultSuccess,
                () => new Metadata(), () => { });
        }

        public override AsyncUnaryCall<RoundAdmissionReply> ActivateRoundAsync(ActivateRoundRequest request,
            Metadata? headers = null, DateTime? deadline = null,
            CancellationToken cancellationToken = default) =>
            new(Task.FromResult(new RoundAdmissionReply { RoomId = request.RoomId, Status = "Running" }),
                Task.FromResult(new Metadata()), () => Status.DefaultSuccess,
                () => new Metadata(), () => { });
    }

    /// <summary>The frame store: a scriptable inbox, every commit request captured.</summary>
    private sealed class ScriptedFrameStore(Func<ReadInboxReply>? inbox = null) : FrameStore.FrameStoreClient
    {
        private readonly object _gate = new();
        private readonly List<CommitFrameRequest> _commits = new();

        public Func<ReadInboxReply>? Inbox { get; set; } = inbox;

        public IReadOnlyList<CommitFrameRequest> Commits
        {
            get { lock (_gate) return _commits.ToList(); }
        }

        public int CommitCount { get { lock (_gate) return _commits.Count; } }

        public override AsyncUnaryCall<ReadInboxReply> ReadInboxAsync(ReadInboxRequest request,
            Metadata? headers = null, DateTime? deadline = null,
            CancellationToken cancellationToken = default) =>
            new(Task.FromResult(Inbox?.Invoke() ?? new ReadInboxReply()),
                Task.FromResult(new Metadata()), () => Status.DefaultSuccess,
                () => new Metadata(), () => { });

        public override AsyncUnaryCall<FrameReceiptReply> CommitFrameAsync(CommitFrameRequest request,
            Metadata? headers = null, DateTime? deadline = null,
            CancellationToken cancellationToken = default)
        {
            lock (_gate) _commits.Add(request);
            return new AsyncUnaryCall<FrameReceiptReply>(
                Task.FromResult(new FrameReceiptReply
                {
                    CommitId = request.CommitId, RoomId = request.RoomId,
                    Tick = request.PrevTick + 1, StateVersion = request.PrevStateVersion + 1
                }),
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

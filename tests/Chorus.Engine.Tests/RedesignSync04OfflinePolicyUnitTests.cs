using Chorus.GameServer;
using Chorus.ExampleGame;
using Chorus.Rpc;
using Google.Protobuf;
using Grpc.Core;

namespace Chorus.Engine.Tests;

/// <summary>
/// FIX-SYNC-04 acceptance (持久钉住离线策略配置): the five offline-policy facts are a
/// module manifest declaration (authoritative-sync.md:44), not a host default — the
/// declaration is validated at admission (illegal count or missing behavior refuses the
/// room), every shipped package declares a legal policy, and the exact canonical form the
/// game host passes to the store is what a later recovery must still match. The recovery
/// pin itself is in <see cref="RedesignModuleIdentityUnitTests"/>; the real-persistence
/// round trip is in <see cref="RedesignSync04OfflinePolicyHostTests"/>.
/// </summary>
public sealed class RedesignSync04OfflinePolicyUnitTests
{
    [Fact]
    public void AValidProductionPolicyPassesForItsParticipantCount()
    {
        Assert.Null(ModuleOfflinePolicy.ProductionDefault.Validate(2));
    }

    [Fact]
    public void AnUnchosenAllOfflineModeIsRefused()
    {
        var policy = ModuleOfflinePolicy.ProductionDefault with { AllOffline = AllOfflinePolicy.Unset };
        Assert.Contains("not chosen", policy.Validate(2), StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnchosenPausedInputPolicyIsRefused()
    {
        var policy = ModuleOfflinePolicy.ProductionDefault with { PausedInput = PausedInputPolicy.Unset };
        Assert.Contains("not chosen", policy.Validate(2), StringComparison.Ordinal);
    }

    [Fact]
    public void AReadyCountBelowOneIsRefused()
    {
        var policy = ModuleOfflinePolicy.ProductionDefault with { ResumeRequiredReadyCount = 0 };
        Assert.Contains("below one", policy.Validate(2), StringComparison.Ordinal);
    }

    [Fact]
    public void AReadyCountAboveTheParticipantsIsRefused()
    {
        var policy = ModuleOfflinePolicy.ProductionDefault with { ResumeRequiredReadyCount = 3 };
        Assert.Contains("exceeds the round's 2", policy.Validate(2), StringComparison.Ordinal);
    }

    [Fact]
    public void AReadyCountEqualToTheParticipantsIsAccepted()
    {
        var policy = ModuleOfflinePolicy.ProductionDefault with { ResumeRequiredReadyCount = 2 };
        Assert.Null(policy.Validate(2));
    }

    [Fact]
    public void ANonPositiveOfflineGraceIsRefused()
    {
        var policy = ModuleOfflinePolicy.ProductionDefault with { OfflineGraceSeconds = 0 };
        Assert.Contains("not positive", policy.Validate(2), StringComparison.Ordinal);
    }

    [Fact]
    public void ANonPositiveMaxPauseIsRefused()
    {
        var policy = ModuleOfflinePolicy.ProductionDefault with { MaxPauseSeconds = 0 };
        Assert.Contains("not positive", policy.Validate(2), StringComparison.Ordinal);
    }

    [Fact]
    public void TheCanonicalFormIsDeclarationExact()
    {
        // The persisted identity is the string itself — renaming a mode or reordering a
        // duration changes it and fails closed, so a later declaration can never alias a
        // different policy onto a room.
        Assert.Equal("ContinueToEnd|30|120|1|Buffer", ModuleOfflinePolicy.ProductionDefault.Canonical);
    }

    [Fact]
    public void EveryShippedPackageDeclaresALegalOfflinePolicy()
    {
        foreach (var package in RedesignModuleCatalog.Shipped)
        {
            var policy = package.ManifestAt(package.TickMs).Offline;
            Assert.NotNull(policy);
            Assert.Null(policy!.Validate(2));
        }
    }

    [Fact]
    public async Task ARoomUnderAPackageWithoutAnOfflinePolicyIsRefusedBeforeAnyStoreCall()
    {
        var rounds = new ScriptedRoundStore();
        await using var runtime = new RoomRuntime(rounds, new ScriptedFrameStore(),
            new ScriptedSettlement(), shippedPackages: [PackageWith(offline: null)]);

        var error = await Assert.ThrowsAsync<ModulePackageException>(
            () => runtime.PrepareRoomAsync(Prepare("room-no-policy", "op-1")));

        Assert.Contains("declares no offline policy", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, rounds.PrepareCalls);
    }

    [Fact]
    public async Task ARoomUnderAnIllegalReadyCountIsRefusedBeforeAnyStoreCall()
    {
        var rounds = new ScriptedRoundStore();
        await using var runtime = new RoomRuntime(rounds, new ScriptedFrameStore(),
            new ScriptedSettlement(),
            shippedPackages: [PackageWith(new ModuleOfflinePolicy(
                AllOfflinePolicy.PauseAtBoundary, 15, 60, 5, PausedInputPolicy.Buffer))]);

        var error = await Assert.ThrowsAsync<ModulePackageException>(
            () => runtime.PrepareRoomAsync(Prepare("room-illegal-count", "op-1")));

        Assert.Contains("exceeds the round's 2", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, rounds.PrepareCalls);
    }

    [Fact]
    public async Task APrepareCarriesTheDeclaredPolicyCanonicalToTheStore()
    {
        // The acceptance leg "configuration enters the room's persisted config": the game
        // host passes the exact canonical declaration the room was validated under — the
        // string the room row persists and a recovery later compares against.
        var rounds = new ScriptedRoundStore();
        string? persisted = null;
        rounds.Prepare = request =>
        {
            persisted = request.OfflinePolicy;
            return Task.FromResult(new RoundAdmissionReply
            {
                RoomId = request.RoomId, Status = "Preparing"
            });
        };
        await using var runtime = new RoomRuntime(rounds, new ScriptedFrameStore(),
            new ScriptedSettlement(),
            shippedPackages: [PackageWith(new ModuleOfflinePolicy(
                AllOfflinePolicy.PauseAtBoundary, 15, 60, 2, PausedInputPolicy.Buffer))]);

        var reply = await runtime.PrepareRoomAsync(
            Prepare("room-policy-carried", "op-1", deferred: true));

        Assert.Equal("Prepared", reply.Status);
        Assert.Equal("PauseAtBoundary|15|60|2|Buffer", persisted);
    }

    private static RedesignModulePackage PackageWith(ModuleOfflinePolicy? offline) => new(
        "example-factions", "1.0.0", "cfg-1", "pkg-1",
        RedesignGameModuleManifest.DeterministicReplay, 25, 100, offline,
        static manifest => new ExampleGameModule(manifest));

    private static PrepareRoomRequest Prepare(string roomId, string operationId, bool deferred = false) => new()
    {
        RoomId = roomId, GameId = "game-a", OwnerInstanceId = "game-1", Epoch = 1,
        OperationId = operationId, DurationTicks = 200, RandomSeed = 777, TickMs = 25,
        DeferredActivation = deferred,
        Participants =
        {
            new GameParticipantProto { ParticipantId = "host-s04", BindingId = "binding-host-s04", Faction = GameStartConfig.RedFaction },
            new GameParticipantProto { ParticipantId = "guest-s04", BindingId = "binding-guest-s04", Faction = GameStartConfig.BlueFaction }
        }
    };

    private sealed class ScriptedRoundStore : RoundStore.RoundStoreClient
    {
        private readonly object _gate = new();
        private int _prepareCalls;
        public Func<PrepareRoundRequest, Task<RoundAdmissionReply>>? Prepare { get; set; }
        public int PrepareCalls { get { lock (_gate) return _prepareCalls; } }

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
    }

    /// <summary>The frame store answering plain successes; a refused admission never
    /// reaches a frame call, and a deferred prepare never ticks.</summary>
    private sealed class ScriptedFrameStore : FrameStore.FrameStoreClient
    {
        public override AsyncUnaryCall<ReadInboxReply> ReadInboxAsync(ReadInboxRequest request,
            Metadata? headers = null, DateTime? deadline = null,
            CancellationToken cancellationToken = default) =>
            new(Task.FromResult(new ReadInboxReply()), Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess, () => new Metadata(), () => { });
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

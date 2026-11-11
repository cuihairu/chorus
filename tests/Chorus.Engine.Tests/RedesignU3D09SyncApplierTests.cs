using System;
using System.Net.Security;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Chorus.UnitySdk;
using Chorus.UnitySdk.Edge;
using Xunit;

namespace Chorus.Engine.Tests;

/// <summary>
/// The snapshot and delta applier (FIX-U3D-09): the leaf's <see cref="SyncApplier"/>
/// ports the server reference applier's ordered rules name for name, and the facts pin
/// the whole decision ladder on constructed frames — apply, drop, rebaseline and invalid
/// each fire for exactly the reason the reference answers, and nothing but an apply
/// moves the baseline. The real link runs end to end against a real match cluster: the
/// stream applies the head snapshot, chains the deltas behind it, a resubscribe
/// supersedes the old stream and rebaselines on a fresh head, a foreign room refuses
/// visibly, and the round's frozen result ends the stream with it.
/// </summary>
public sealed class RedesignU3D09SyncApplierTests
{
    private const string Room = "room-1";
    private const long Epoch = 3;

    // ---------------------------------------------------------------------
    // The ordered decision ladder (constructed frames, leaf only).
    // ---------------------------------------------------------------------

    [Fact]
    public void AFirstSnapshotAppliesAndStampsTheBaseline()
    {
        var applier = new SyncApplier(Room, Epoch, DeltaContractMode.Full);

        var decision = applier.Decide(SnapshotLike());

        Assert.Equal(SyncDecisionKind.ApplySnapshot, decision.Kind);
        Assert.Null(decision.Reason);
        var status = applier.Status;
        Assert.True(status.HasBaseline);
        Assert.False(status.Terminal);
        Assert.Equal(Room, status.RoomId);
        Assert.Equal(Epoch, status.Epoch);
        Assert.Equal(5, status.Tick);
        Assert.Equal(2, status.StateVersion);
        Assert.Equal("module-1", status.ModuleVersion);
        Assert.Equal("cfg-1", status.ConfigHash);
    }

    [Fact]
    public void AFirstDeltaWithoutABaselineAsksForOne()
    {
        var applier = new SyncApplier(Room, Epoch, DeltaContractMode.Full);

        var decision = applier.Decide(DeltaLike(version: 1, baseVersion: 0, tick: 1));

        Assert.Equal(SyncDecisionKind.RebaselineRequired, decision.Kind);
        Assert.Equal(SyncReasons.MissingBaseline, decision.Reason);
        var status = applier.Status;
        Assert.False(status.HasBaseline);
        Assert.Equal(-1, status.Tick);
        Assert.Equal(-1, status.StateVersion);
    }

    [Fact]
    public void ChainedDeltasAdvanceAndAPauseBoundaryIsLegal()
    {
        var applier = Applied();

        Assert.Equal(SyncDecisionKind.ApplyDelta, applier.Decide(DeltaLike(version: 3, baseVersion: 2, tick: 6)).Kind);
        Assert.Equal(3, applier.Status.StateVersion);
        Assert.Equal(6, applier.Status.Tick);
        // A version can advance while the tick pauses.
        Assert.Equal(SyncDecisionKind.ApplyDelta, applier.Decide(DeltaLike(version: 4, baseVersion: 3, tick: 6)).Kind);
        Assert.Equal(4, applier.Status.StateVersion);
        Assert.Equal(6, applier.Status.Tick);
    }

    [Fact]
    public void AGapAsksForASnapshot()
    {
        var applier = Applied();

        var decision = applier.Decide(DeltaLike(version: 5, baseVersion: 4, tick: 8));

        Assert.Equal(SyncDecisionKind.RebaselineRequired, decision.Kind);
        Assert.Equal(SyncReasons.Gap, decision.Reason);
        AssertBaselineStays(applier, tick: 5, version: 2);
    }

    [Fact]
    public void ARegressionAndADuplicateDropLeaveTheBaselineUntouched()
    {
        var applier = Applied(version: 3, tick: 5);
        var before = applier.Status;

        Assert.Equal((SyncDecisionKind.Drop, SyncReasons.Regression),
            KindAndReason(applier.Decide(DeltaLike(version: 2, baseVersion: 1, tick: 5))));
        Assert.Equal((SyncDecisionKind.Drop, SyncReasons.Duplicate),
            KindAndReason(applier.Decide(DeltaLike(version: 3, baseVersion: 2, tick: 5))));
        Assert.Equal((SyncDecisionKind.Drop, SyncReasons.Regression),
            KindAndReason(applier.Decide(SnapshotLike(version: 2))));
        Assert.Equal((SyncDecisionKind.Drop, SyncReasons.Duplicate),
            KindAndReason(applier.Decide(SnapshotLike(version: 3))));

        AssertBaselineStays(applier, before.Tick, before.StateVersion);
    }

    [Fact]
    public void ACrossEpochNeverChainsOntoTheOldBaseline()
    {
        var applier = Applied();

        Assert.Equal((SyncDecisionKind.RebaselineRequired, SyncReasons.CrossEpoch),
            KindAndReason(applier.Decide(SnapshotLike(epoch: 4, version: 9))));
        var snapshot = applier.Decide(SnapshotLike(epoch: 4, version: 9));
        Assert.Equal(4, snapshot.TargetEpoch);
        Assert.Equal((SyncDecisionKind.RebaselineRequired, SyncReasons.CrossEpoch),
            KindAndReason(applier.Decide(DeltaLike(version: 10, baseVersion: 9, tick: 12, epoch: 4))));
        Assert.Equal((SyncDecisionKind.RebaselineRequired, SyncReasons.CrossEpoch),
            KindAndReason(applier.Decide(ResultLike(version: 10, tick: 12, epoch: 4))));

        AssertBaselineStays(applier, tick: 5, version: 2);
        Assert.Equal(Epoch, applier.Status.Epoch);
    }

    [Fact]
    public void AForeignRoomIsAHardViolationOnEveryMember()
    {
        var applier = Applied();

        Assert.Equal((SyncDecisionKind.Invalid, SyncReasons.ProtocolRoomMismatch),
            KindAndReason(applier.Decide(SnapshotLike(room: "room-2"))));
        Assert.Equal((SyncDecisionKind.Invalid, SyncReasons.ProtocolRoomMismatch),
            KindAndReason(applier.Decide(DeltaLike(version: 3, baseVersion: 2, tick: 6, room: "room-2"))));
        Assert.Equal((SyncDecisionKind.Invalid, SyncReasons.ProtocolRoomMismatch),
            KindAndReason(applier.Decide(ResultLike(version: 3, tick: 6, room: "room-2"))));
        Assert.Equal((SyncDecisionKind.Invalid, SyncReasons.ProtocolRoomMismatch),
            KindAndReason(applier.Decide(ResyncLike(currentEpoch: 3, room: "room-2"))));

        AssertBaselineStays(applier, tick: 5, version: 2);
    }

    [Fact]
    public void OutOfRangeVersionsAreHardViolations()
    {
        var applier = Applied();

        Assert.Equal((SyncDecisionKind.Invalid, SyncReasons.ProtocolVersionRange),
            KindAndReason(applier.Decide(SnapshotLike(version: 0))));
        Assert.Equal((SyncDecisionKind.Invalid, SyncReasons.ProtocolVersionRange),
            KindAndReason(applier.Decide(SnapshotLike(version: 1, tick: -1))));
        Assert.Equal((SyncDecisionKind.Invalid, SyncReasons.ProtocolVersionRange),
            KindAndReason(applier.Decide(DeltaLike(version: 0, baseVersion: -1, tick: 1))));
        Assert.Equal((SyncDecisionKind.Invalid, SyncReasons.ProtocolVersionRange),
            KindAndReason(applier.Decide(DeltaLike(version: 1, baseVersion: 0, tick: -1))));
        Assert.Equal((SyncDecisionKind.Invalid, SyncReasons.ProtocolVersionRange),
            KindAndReason(applier.Decide(ResultLike(version: 0, tick: 1))));

        AssertBaselineStays(applier, tick: 5, version: 2);
    }

    [Fact]
    public void AModuleIdentityMismatchIsAHardViolation()
    {
        var applier = Applied();

        Assert.Equal((SyncDecisionKind.Invalid, SyncReasons.ProtocolModuleConfigMismatch),
            KindAndReason(applier.Decide(SnapshotLike(version: 3, module: "module-2"))));
        Assert.Equal((SyncDecisionKind.Invalid, SyncReasons.ProtocolModuleConfigMismatch),
            KindAndReason(applier.Decide(DeltaLike(version: 3, baseVersion: 2, tick: 6, cfg: "cfg-2"))));

        AssertBaselineStays(applier, tick: 5, version: 2);
    }

    [Fact]
    public void ADeltaOutsideTheCommitInvariantIsAHardViolation()
    {
        var applier = Applied();

        // stateVersion must be baseVersion + 1 — the server commits both in one
        // transaction, so anything else is corruption, not a gap.
        Assert.Equal((SyncDecisionKind.Invalid, SyncReasons.ProtocolDeltaInvariant),
            KindAndReason(applier.Decide(DeltaLike(version: 5, baseVersion: 3, tick: 8))));
        Assert.Equal((SyncDecisionKind.Invalid, SyncReasons.ProtocolDeltaInvariant),
            KindAndReason(applier.Decide(DeltaLike(version: 3, baseVersion: 1, tick: 6))));

        AssertBaselineStays(applier, tick: 5, version: 2);
    }

    [Fact]
    public void ATickRegressionIsAHardViolation()
    {
        var applied = Applied();

        Assert.Equal((SyncDecisionKind.Invalid, SyncReasons.ProtocolTickRegression),
            KindAndReason(applied.Decide(DeltaLike(version: 3, baseVersion: 2, tick: 4))));

        // A snapshot that advances the version while rewinding the tick.
        Assert.Equal((SyncDecisionKind.Invalid, SyncReasons.ProtocolTickRegression),
            KindAndReason(applied.Decide(SnapshotLike(version: 3, tick: 4))));

        AssertBaselineStays(applied, tick: 5, version: 2);
    }

    [Fact]
    public void ASnapshotOnlyModuleRefusesDeltasBeforeAskingForABaseline()
    {
        var applier = new SyncApplier(Room, Epoch, DeltaContractMode.SnapshotOnly);

        var decision = applier.Decide(DeltaLike(version: 1, baseVersion: 0, tick: 1));

        // The contract refusal precedes the missing baseline: a module that syncs by
        // snapshots alone hears why, not a generic request for a snapshot.
        Assert.Equal(SyncDecisionKind.RebaselineRequired, decision.Kind);
        Assert.Equal(SyncReasons.NoDeltaContract, decision.Reason);
        Assert.False(applier.Status.HasBaseline);
    }

    [Fact]
    public void ARoundResultTerminatesEvenBareAndThenEverythingDrops()
    {
        var applier = new SyncApplier(Room, Epoch, DeltaContractMode.Full);

        var decision = applier.Decide(ResultLike(version: 1, tick: 9));

        Assert.Equal(SyncDecisionKind.ApplyResult, decision.Kind);
        var status = applier.Status;
        Assert.True(status.Terminal);
        Assert.Equal(9, status.Tick);
        Assert.Equal(1, status.StateVersion);
        // A bare result carries no state baseline: the terminal marker rides alone.
        Assert.False(status.HasBaseline);

        Assert.Equal((SyncDecisionKind.Drop, SyncReasons.Duplicate),
            KindAndReason(applier.Decide(SnapshotLike(version: 2))));
        Assert.Equal((SyncDecisionKind.Drop, SyncReasons.Duplicate),
            KindAndReason(applier.Decide(DeltaLike(version: 2, baseVersion: 1, tick: 10))));
        Assert.Equal((SyncDecisionKind.Drop, SyncReasons.Duplicate),
            KindAndReason(applier.Decide(ResultLike(version: 2, tick: 10))));
    }

    [Fact]
    public void AResultBehindTheBaselineDrops()
    {
        var applier = Applied();

        Assert.Equal((SyncDecisionKind.Drop, SyncReasons.Regression),
            KindAndReason(applier.Decide(ResultLike(version: 1, tick: 3))));
        AssertBaselineStays(applier, tick: 5, version: 2);

        Assert.Equal(SyncDecisionKind.ApplyResult, applier.Decide(ResultLike(version: 5, tick: 9)).Kind);
        Assert.True(applier.Status.Terminal);
    }

    [Fact]
    public void AResyncOrderAsksForTheCurrentEpoch()
    {
        var applier = Applied();

        var decision = applier.Decide(ResyncLike(currentEpoch: 7));

        Assert.Equal(SyncDecisionKind.RebaselineRequired, decision.Kind);
        Assert.Equal(SyncReasons.Resync, decision.Reason);
        Assert.Equal(7, decision.TargetEpoch);
        AssertBaselineStays(applier, tick: 5, version: 2);
    }

    [Fact]
    public void AnEmptyFrameIsAHardViolation()
    {
        var applier = Applied();

        var decision = applier.Decide(new SyncFrame());

        Assert.Equal(SyncDecisionKind.Invalid, decision.Kind);
        Assert.Equal(SyncReasons.ProtocolEmptyMessage, decision.Reason);
    }

    // ---------------------------------------------------------------------
    // The real link: a real match cluster, the SDK surface, the edge wire.
    // ---------------------------------------------------------------------

    [Fact]
    public async Task TheStreamAppliesTheHeadSnapshotAndTheChainedDeltasOnARealRoom()
    {
        await using var environment = await RedesignM0HostTests.HostEnvironment.CreateAsync();
        var (wssPort, simulator) = await RedesignM4HostTests.StartEdgeClusterAsync(environment,
            withMatchmaker: true);
        var certs = Trusts(environment.RootCertificatePem, environment.NodeAuthorityCertificatePem);

        var first = await LoginAndStartAsync(wssPort, simulator, "open-u3d09-a", certs);
        var second = await LoginAndStartAsync(wssPort, simulator, "open-u3d09-b", certs);
        await first.JoinMatchAsync("standard", null, CancellationToken.None);
        await second.JoinMatchAsync("standard", null, CancellationToken.None);
        var room = await first.AwaitRoomAsync(CancellationToken.None);
        await second.AwaitRoomAsync(CancellationToken.None);

        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var applier = new SyncApplier(room.RoomId, room.RoomEpoch, DeltaContractMode.Full);
        var stream = await first.SubscribeAsync(room.RoomId, room.RoomEpoch, 0, 0, budget.Token);

        // The first sync frame is the head snapshot, and the module identity rides it —
        // announced by roomJoined, stamped by the transport, answered by the baseline.
        Assert.True(await stream.MoveNextAsync(budget.Token));
        Assert.Equal(SyncFrameKind.Snapshot, stream.Current.Kind);
        Assert.Equal(SyncDecisionKind.ApplySnapshot, applier.Decide(stream.Current).Kind);
        Assert.Equal(room.ModuleVersion, applier.Status.ModuleVersion);
        Assert.Equal(room.ConfigHash, applier.Status.ConfigHash);
        Assert.True(applier.Status.HasBaseline);

        // The deltas chain: every delta that applies did so at exactly baseline + 1 on
        // the version it named as its base, carrying the room's module identity.
        var sawChainedDelta = false;
        while (!sawChainedDelta)
        {
            Assert.True(await stream.MoveNextAsync(budget.Token));
            var frameRoom = stream.Current.Snapshot?.RoomId ?? stream.Current.Delta?.RoomId;
            if (frameRoom is not null) Assert.Equal(room.RoomId, frameRoom);
            var before = applier.Status;
            var decision = applier.Decide(stream.Current);
            if (decision.Kind != SyncDecisionKind.ApplyDelta) continue;
            var delta = stream.Current.Delta!;
            Assert.Equal(before.StateVersion + 1, delta.StateVersion);
            Assert.Equal(before.StateVersion, delta.BaseVersion);
            Assert.Equal(room.ModuleVersion, delta.ModuleVersion);
            Assert.Equal(room.ConfigHash, delta.ConfigHash);
            Assert.Equal(before.ModuleVersion, applier.Status.ModuleVersion);
            sawChainedDelta = true;
        }

        await first.DisconnectAsync();
        await second.DisconnectAsync();
    }

    [Fact]
    public async Task AResubscribeSupersedesTheOldStreamAndRebaselines()
    {
        await using var environment = await RedesignM0HostTests.HostEnvironment.CreateAsync();
        var (wssPort, simulator) = await RedesignM4HostTests.StartEdgeClusterAsync(environment,
            withMatchmaker: true);
        var certs = Trusts(environment.RootCertificatePem, environment.NodeAuthorityCertificatePem);

        var first = await LoginAndStartAsync(wssPort, simulator, "open-u3d09-c", certs);
        var second = await LoginAndStartAsync(wssPort, simulator, "open-u3d09-d", certs);
        await first.JoinMatchAsync("standard", null, CancellationToken.None);
        await second.JoinMatchAsync("standard", null, CancellationToken.None);
        var room = await first.AwaitRoomAsync(CancellationToken.None);
        await second.AwaitRoomAsync(CancellationToken.None);

        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var stream = await first.SubscribeAsync(room.RoomId, room.RoomEpoch, 0, 0, budget.Token);
        Assert.True(await stream.MoveNextAsync(budget.Token));

        // A newer subscribe owns the downlink from here on; the superseded stream
        // refuses to advance instead of racing its successor for frames.
        var replacement = await first.SubscribeAsync(room.RoomId, room.RoomEpoch, 0, 0, budget.Token);
        await Assert.ThrowsAsync<InvalidOperationException>(() => stream.MoveNextAsync(budget.Token));

        // The re-baseline order brings the room's head back: a fresh applier applies it.
        var fresh = new SyncApplier(room.RoomId, room.RoomEpoch, DeltaContractMode.Full);
        var rebaselined = false;
        while (!rebaselined)
        {
            Assert.True(await replacement.MoveNextAsync(budget.Token));
            if (replacement.Current.Kind == SyncFrameKind.Snapshot)
            {
                Assert.Equal(SyncDecisionKind.ApplySnapshot, fresh.Decide(replacement.Current).Kind);
                rebaselined = true;
            }
            else
            {
                fresh.Decide(replacement.Current);
            }
        }

        await first.DisconnectAsync();
        await second.DisconnectAsync();
    }

    [Fact]
    public async Task ASubscriptionToARoomTheSeatNeverJoinedRefusesVisibly()
    {
        await using var environment = await RedesignM0HostTests.HostEnvironment.CreateAsync();
        var (wssPort, simulator) = await RedesignM4HostTests.StartEdgeClusterAsync(environment);
        var certs = Trusts(environment.RootCertificatePem, environment.NodeAuthorityCertificatePem);
        var session = await LoginAndStartAsync(wssPort, simulator, "open-u3d09-e", certs);

        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var stream = await session.SubscribeAsync("room-never", 0, 0, 0, budget.Token);
        var failure = await Assert.ThrowsAsync<EdgeProtocolException>(() => stream.MoveNextAsync(budget.Token));
        // The edge's own refusal vocabulary for a room this connection never sat in.
        Assert.Equal("invalid_message", failure.Code);
        await session.DisconnectAsync();
    }

    [Fact]
    public async Task TheRoundEndsFrozenAndTheStreamEndsWithIt()
    {
        await using var environment = await RedesignM0HostTests.HostEnvironment.CreateAsync();
        var (wssPort, simulator) = await RedesignM4HostTests.StartEdgeClusterAsync(environment,
            withMatchmaker: true);
        var certs = Trusts(environment.RootCertificatePem, environment.NodeAuthorityCertificatePem);

        var first = await LoginAndStartAsync(wssPort, simulator, "open-u3d09-f", certs);
        var second = await LoginAndStartAsync(wssPort, simulator, "open-u3d09-g", certs);
        await first.JoinMatchAsync("standard", null, CancellationToken.None);
        await second.JoinMatchAsync("standard", null, CancellationToken.None);
        var room = await first.AwaitRoomAsync(CancellationToken.None);
        await second.AwaitRoomAsync(CancellationToken.None);

        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var applier = new SyncApplier(room.RoomId, room.RoomEpoch, DeltaContractMode.Full);
        var stream = await first.SubscribeAsync(room.RoomId, room.RoomEpoch, 0, 0, budget.Token);

        while (true)
        {
            var advanced = await stream.MoveNextAsync(budget.Token);
            var decision = applier.Decide(stream.Current);
            if (decision.Kind == SyncDecisionKind.ApplyResult) break;
            Assert.True(advanced, "the stream ended before the round's frozen result.");
        }

        // The frozen result: terminal, a real result payload, positioned at the chain
        // this stream actually saw.
        Assert.True(applier.Status.Terminal);
        Assert.False(string.IsNullOrEmpty(stream.Current.RoundResult!.ResultJson));
        using (JsonDocument.Parse(stream.Current.RoundResult.ResultJson))
        {
        }
        Assert.True(stream.Current.RoundResult.StateVersion >= 1);

        // The round's story is told — the stream ends with it.
        Assert.False(await stream.MoveNextAsync(budget.Token));

        await first.DisconnectAsync();
        await second.DisconnectAsync();
    }

    // ---------------------------------------------------------------------
    // Fixtures and frame builders.
    // ---------------------------------------------------------------------

    /// <summary>An applier holding one applied snapshot (tick 5, version 2) unless the
    /// caller names another position.</summary>
    private static SyncApplier Applied(long version = 2, long tick = 5)
    {
        var applier = new SyncApplier(Room, Epoch, DeltaContractMode.Full);
        var applied = applier.Decide(SnapshotLike(version: version, tick: tick));
        Assert.Equal(SyncDecisionKind.ApplySnapshot, applied.Kind);
        return applier;
    }

    private static void AssertBaselineStays(SyncApplier applier, long tick, long version)
    {
        var status = applier.Status;
        Assert.True(status.HasBaseline);
        Assert.False(status.Terminal);
        Assert.Equal(tick, status.Tick);
        Assert.Equal(version, status.StateVersion);
    }

    private static (SyncDecisionKind Kind, string Reason) KindAndReason(SyncDecision decision) =>
        (decision.Kind, decision.Reason!);

    private static SyncFrame SnapshotLike(string room = Room, long epoch = Epoch, long tick = 5,
        long version = 2, string module = "module-1", string cfg = "cfg-1") => new()
    {
        Kind = SyncFrameKind.Snapshot,
        Snapshot = new SnapshotFrame
        {
            RoomId = room,
            Epoch = epoch,
            Tick = tick,
            StateVersion = version,
            ModuleVersion = module,
            ConfigHash = cfg,
            StatePayload = new byte[] { 1 },
        },
    };

    private static SyncFrame DeltaLike(long version, long baseVersion, long tick,
        string room = Room, long epoch = Epoch, string module = "module-1", string cfg = "cfg-1") => new()
    {
        Kind = SyncFrameKind.Delta,
        Delta = new DeltaFrame
        {
            RoomId = room,
            Epoch = epoch,
            Tick = tick,
            StateVersion = version,
            BaseVersion = baseVersion,
            ModuleVersion = module,
            ConfigHash = cfg,
            Effects = [new FrameEffect { Kind = "score", PayloadJson = """{"delta":1}""" }],
        },
    };

    private static SyncFrame ResultLike(long version, long tick, string room = Room, long epoch = Epoch) => new()
    {
        Kind = SyncFrameKind.RoundResult,
        RoundResult = new RoundResultFrame
        {
            RoomId = room,
            Epoch = epoch,
            Tick = tick,
            StateVersion = version,
            ResultJson = """{"winner":"home"}""",
        },
    };

    private static SyncFrame ResyncLike(long currentEpoch, string room = Room) => new()
    {
        Kind = SyncFrameKind.ResyncRequired,
        ResyncRequired = new ResyncRequiredFrame
        {
            RoomId = room,
            CurrentEpoch = currentEpoch,
            Reason = "chain-broken",
        },
    };

    private static async Task<ChorusSession> LoginAndStartAsync(int wssPort,
        Chorus.Platform.DevelopmentPlatformSimulator simulator, string openId,
        RemoteCertificateValidationCallback certs)
    {
        var credential = simulator.IssueCredential("app-1", openId, "live-" + openId, TimeSpan.FromMinutes(5));
        var adapter = RedesignU3D07LoginLinkTests.FakeAdapterLike("simulator", openId, credential);
        var sdk = new ChorusSdk(configuration => new EdgeChorusTransport(configuration, certs));
        var configuration = RedesignU3D07LoginLinkTests.ConfigurationLike(adapter, wssPort);
        return await sdk.ConnectAsync(configuration, adapter, CancellationToken.None);
    }

    private static RemoteCertificateValidationCallback Trusts(string rootPem, string nodeAuthorityPem)
    {
        // The certs are NOT disposed: the closure must keep them alive for every
        // handshake this transport runs.
        var root = System.Security.Cryptography.X509Certificates.X509Certificate2.CreateFromPem(rootPem);
        var nodeAuthority = System.Security.Cryptography.X509Certificates.X509Certificate2.CreateFromPem(
            nodeAuthorityPem);
        return (_, certificate, _, _) =>
        {
            if (certificate is null) return false;
            using var candidate = new System.Security.Cryptography.X509Certificates.X509Certificate2(certificate);
            using var validator = new System.Security.Cryptography.X509Certificates.X509Chain();
            validator.ChainPolicy.TrustMode =
                System.Security.Cryptography.X509Certificates.X509ChainTrustMode.CustomRootTrust;
            validator.ChainPolicy.RevocationMode =
                System.Security.Cryptography.X509Certificates.X509RevocationMode.NoCheck;
            validator.ChainPolicy.CustomTrustStore.Add(root);
            validator.ChainPolicy.ExtraStore.Add(nodeAuthority);
            return validator.Build(candidate);
        };
    }
}

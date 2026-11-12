using System.Security.Cryptography;
using Chorus.ControlServer;
using Chorus.Data;
using Chorus.DataServer;
using Chorus.Rpc;
using Grpc.Core;
using Npgsql;

namespace Chorus.Engine.Tests;

/// <summary>
/// The admin face driven as plain method calls: the operator identity comes from the bearer
/// credential in the request metadata (never the request body, never the process
/// certificate), the role and scope fences are the FIX-GM-06 rules, every command is audited
/// and idempotent per request id, and the data-plane legs (round-input lookup, settlement,
/// platform tasks, the leaderboard board) are reached only past those fences — a dead
/// endpoint answers exactly there.
/// </summary>
public sealed class RedesignAdminGrpcFaceTests
{
    private const string DeadEndpoint = "https://127.0.0.1:9";

    [Fact]
    public async Task ReadyAnswersForTheControlServer()
    {
        await using var rig = await Rig.CreateAsync();
        var reply = await rig.Service.Ready(new Empty(), rig.Context("@cluster", "control-server", "control-1"));
        Assert.True(reply.Ready);
        Assert.Equal("4", reply.SchemaVersion);
    }

    [Fact]
    public async Task ReadyRefusesAnAbsentCertificate()
    {
        await using var rig = await Rig.CreateAsync();
        await Rejects(() => rig.Service.Ready(new Empty(), rig.Certificates.Context(null)),
            StatusCode.Unauthenticated, "Client certificate is absent from request context.");
    }

    [Fact]
    public async Task ReadyRefusesAForeignCertificate()
    {
        await using var rig = await Rig.CreateAsync();
        await Rejects(() => rig.Service.Ready(new Empty(),
                rig.Certificates.Context(rig.Certificates.ForeignClusterCertificate())),
            StatusCode.Unauthenticated, "Client certificate identity is invalid.");
    }

    [Fact]
    public async Task ReadyRefusesARoleOutsideTheControlPlane()
    {
        await using var rig = await Rig.CreateAsync();
        await Rejects(() => rig.Service.Ready(new Empty(), rig.Context("@cluster", "data-server", "data-1")),
            StatusCode.PermissionDenied, "Role is not authorized.");
    }

    [Fact]
    public async Task WhoAmIAnswersTheAuthenticatedOperator()
    {
        await using var rig = await Rig.CreateAsync();
        var reply = await rig.Service.WhoAmI(new Empty(), rig.Operator("token-gm"));
        Assert.Equal("gm-1", reply.OperatorId);
        Assert.Equal("gm", reply.Role);
        Assert.Equal(["tenant-a"], reply.Tenants);
        Assert.Equal(["game-a"], reply.Games);
        Assert.True(reply.Enabled);
    }

    [Fact]
    public async Task WhoAmIRefusesAnAbsentCredential()
    {
        await using var rig = await Rig.CreateAsync();
        await Rejects(() => rig.Service.WhoAmI(new Empty(), rig.Certificates.Context(null)),
            StatusCode.Unauthenticated, "Operator credentials are absent.");
    }

    [Fact]
    public async Task WhoAmIRefusesAnUnknownCredential()
    {
        await using var rig = await Rig.CreateAsync();
        await Rejects(() => rig.Service.WhoAmI(new Empty(), rig.Operator("token-none")),
            StatusCode.Unauthenticated, "Operator credential is invalid.");
    }

    [Fact]
    public async Task GetRoomEpochAnswersFromTheOwnershipRow()
    {
        await using var rig = await Rig.CreateAsync();
        await rig.SeedOwnershipAsync("room-epoch-1", "gs-1", epoch: 2);
        var reply = await rig.Service.GetRoomEpoch(new AdminGetRoomEpochRequest
        {
            TenantId = "tenant-a", GameId = "game-a", RoomId = "room-epoch-1", Operator = "gm-1"
        }, rig.Operator("token-gm"));
        Assert.True(reply.Found);
        Assert.Equal(2, reply.Epoch);
        Assert.Equal("gs-1", reply.OwnerInstanceId);
        Assert.Equal("Active", reply.Status);
    }

    [Fact]
    public async Task GetRoomEpochRefusesAnIncompleteRequest()
    {
        await using var rig = await Rig.CreateAsync();
        await Rejects(() => rig.Service.GetRoomEpoch(new AdminGetRoomEpochRequest
        {
            TenantId = "tenant-a", GameId = "game-a", RoomId = ""
        }, rig.Operator("token-gm")),
            StatusCode.InvalidArgument, "The epoch request is incomplete.");
    }

    [Fact]
    public async Task GetRoomEpochRefusesAForgedOperator()
    {
        await using var rig = await Rig.CreateAsync();
        await rig.SeedOwnershipAsync("room-epoch-2", "gs-1", epoch: 1);
        await Rejects(() => rig.Service.GetRoomEpoch(new AdminGetRoomEpochRequest
        {
            TenantId = "tenant-a", GameId = "game-a", RoomId = "room-epoch-2", Operator = "someone-else"
        }, rig.Operator("token-gm")),
            StatusCode.PermissionDenied, "The request operator does not match the authenticated operator.");
    }

    [Fact]
    public async Task GetRoomEpochRefusesAScopeOutsideTheOperator()
    {
        await using var rig = await Rig.CreateAsync();
        await rig.SeedOwnershipAsync("room-epoch-3", "gs-1", epoch: 1);
        await Rejects(() => rig.Service.GetRoomEpoch(new AdminGetRoomEpochRequest
        {
            TenantId = "tenant-b", GameId = "game-a", RoomId = "room-epoch-3", Operator = "gm-1"
        }, rig.Operator("token-gm")),
            StatusCode.PermissionDenied, "Operator scope does not cover the target.");
    }

    [Fact]
    public async Task GetRoomEpochRefusesARoleBelowTheFloor()
    {
        await using var rig = await Rig.CreateAsync();
        await Rejects(() => rig.Service.GetRoomEpoch(new AdminGetRoomEpochRequest
        {
            TenantId = "tenant-a", GameId = "game-a", RoomId = "room-epoch-4", Operator = "viewer-1"
        }, rig.Operator("token-viewer")),
            StatusCode.PermissionDenied, "Operator role is not authorized.");
    }

    [Fact]
    public async Task BumpRoomEpochMovesTheEpochAndReportsTheRoute()
    {
        await using var rig = await Rig.CreateAsync();
        await rig.SeedRunningRoomAsync("room-bump-1", epoch: 1);
        var reply = await rig.Service.BumpRoomEpoch(new AdminBumpRoomEpochRequest
        {
            RequestId = "bump-1", Operator = "gm-1", Reason = "suspect frame",
            TenantId = "tenant-a", GameId = "game-a", RoomId = "room-bump-1",
            ExpectedEpoch = 1, TargetEpoch = 0
        }, rig.Operator("token-gm"));
        Assert.Equal("bump-1", reply.OperationId);
        Assert.Equal("gs-1", reply.OwnerInstanceId);
        Assert.Equal(1, reply.OldEpoch);
        Assert.Equal(2, reply.NewEpoch);
        Assert.Equal("Recovering", reply.OwnershipStatus);
        // The storage committed and the room's new epoch moved the snapshot version, but no
        // gateway confirmed it — "Pending", never "Synced" before the confirmation.
        Assert.Equal("Pending", reply.RouteSyncStatus);
        Assert.False(reply.Duplicate);
    }

    [Fact]
    public async Task BumpRoomEpochReplaysAsAVisibleDuplicate()
    {
        await using var rig = await Rig.CreateAsync();
        await rig.SeedRunningRoomAsync("room-bump-2", epoch: 1);
        var request = new AdminBumpRoomEpochRequest
        {
            RequestId = "bump-2", Operator = "gm-1", Reason = "suspect frame",
            TenantId = "tenant-a", GameId = "game-a", RoomId = "room-bump-2",
            ExpectedEpoch = 1, TargetEpoch = 0
        };
        await rig.Service.BumpRoomEpoch(request, rig.Operator("token-gm"));
        var replay = await rig.Service.BumpRoomEpoch(request, rig.Operator("token-gm"));
        Assert.True(replay.Duplicate);
        Assert.Equal(1, replay.OldEpoch);
        Assert.Equal(2, replay.NewEpoch);
    }

    [Fact]
    public async Task BumpRoomEpochRefusesASpentRequestIdUnderADifferentShape()
    {
        await using var rig = await Rig.CreateAsync();
        await rig.SeedRunningRoomAsync("room-bump-3", epoch: 1);
        await rig.Service.BumpRoomEpoch(new AdminBumpRoomEpochRequest
        {
            RequestId = "bump-3", Operator = "gm-1", Reason = "suspect frame",
            TenantId = "tenant-a", GameId = "game-a", RoomId = "room-bump-3",
            ExpectedEpoch = 1, TargetEpoch = 0
        }, rig.Operator("token-gm"));
        await Rejects(() => rig.Service.BumpRoomEpoch(new AdminBumpRoomEpochRequest
        {
            RequestId = "bump-3", Operator = "gm-1", Reason = "suspect frame",
            TenantId = "tenant-a", GameId = "game-a", RoomId = "room-bump-3",
            ExpectedEpoch = 1, TargetEpoch = 9
        }, rig.Operator("token-gm")),
            StatusCode.AlreadyExists, "The request id is already spent by a different request.");
    }

    [Fact]
    public async Task BumpRoomEpochRefusesAnEpochDisagreement()
    {
        await using var rig = await Rig.CreateAsync();
        await rig.SeedRunningRoomAsync("room-bump-4", epoch: 1);
        await Rejects(() => rig.Service.BumpRoomEpoch(new AdminBumpRoomEpochRequest
        {
            RequestId = "bump-4", Operator = "gm-1", Reason = "suspect frame",
            TenantId = "tenant-a", GameId = "game-a", RoomId = "room-bump-4",
            ExpectedEpoch = 9, TargetEpoch = 0
        }, rig.Operator("token-gm")),
            StatusCode.FailedPrecondition, "The ownership epoch is 1, not 9.");
    }

    [Fact]
    public async Task BumpRoomEpochRefusesAnIncompleteRequest()
    {
        await using var rig = await Rig.CreateAsync();
        await Rejects(() => rig.Service.BumpRoomEpoch(new AdminBumpRoomEpochRequest
        {
            RequestId = "", Operator = "gm-1", Reason = "suspect frame",
            TenantId = "tenant-a", GameId = "game-a", RoomId = "room-bump-5",
            ExpectedEpoch = 1, TargetEpoch = 0
        }, rig.Operator("token-gm")),
            StatusCode.InvalidArgument, "The bump request is incomplete.");
    }

    [Fact]
    public async Task TheQueueGateSuspendsResumesAndAnswersDuplicates()
    {
        await using var rig = await Rig.CreateAsync();
        const string partition = "tenant-a/game-a/rules-1/region-1/simulator/standard";
        var suspended = await rig.Service.SuspendMatchQueue(AdminQueueGate(partition, "gate-1"),
            rig.Operator("token-gm"));
        Assert.True(suspended.Suspended);
        Assert.False(suspended.Duplicate);
        var replay = await rig.Service.SuspendMatchQueue(AdminQueueGate(partition, "gate-1"),
            rig.Operator("token-gm"));
        Assert.True(replay.Suspended);
        Assert.True(replay.Duplicate);
        var resumed = await rig.Service.ResumeMatchQueue(AdminQueueGate(partition, "gate-2"),
            rig.Operator("token-gm"));
        Assert.False(resumed.Suspended);
        Assert.False(resumed.Duplicate);
    }

    [Fact]
    public async Task TheQueueGateRefusesAnIncompleteCommand()
    {
        await using var rig = await Rig.CreateAsync();
        await Rejects(() => rig.Service.SuspendMatchQueue(AdminQueueGate("", "gate-3"),
                rig.Operator("token-gm")),
            StatusCode.InvalidArgument,
            "The command needs a request id, an operator, a reason and a partition.");
        await Rejects(() => rig.Service.SuspendMatchQueue(
                AdminQueueGate("tenant-a/game-a/rules-1", "gate-4", operatorName: "someone-else"),
                rig.Operator("token-gm")),
            StatusCode.PermissionDenied, "The request operator does not match the authenticated operator.");
    }

    [Fact]
    public async Task TheQueueGateRefusesARoleBelowTheFloor()
    {
        await using var rig = await Rig.CreateAsync();
        await Rejects(() => rig.Service.SuspendMatchQueue(
                AdminQueueGate("tenant-a/game-a/rules-1", "gate-5"), rig.Operator("token-viewer")),
            StatusCode.PermissionDenied, "Operator role is not authorized.");
    }

    [Fact]
    public async Task DrainProcessParksTheProcessAndAnswersDuplicates()
    {
        await using var rig = await Rig.CreateAsync();
        await rig.SeedProcessAsync("proc-1");
        var drained = await rig.Service.DrainProcess(new AdminDrainProcessRequest
        {
            RequestId = "drain-1", Operator = "gm-1", Reason = "noisy neighbor", ProcessInstanceId = "proc-1"
        }, rig.Operator("token-gm"));
        Assert.True(drained.Found);
        Assert.Equal("Draining", drained.Lifecycle);
        Assert.False(drained.Duplicate);
        var replay = await rig.Service.DrainProcess(new AdminDrainProcessRequest
        {
            RequestId = "drain-1", Operator = "gm-1", Reason = "noisy neighbor", ProcessInstanceId = "proc-1"
        }, rig.Operator("token-gm"));
        Assert.True(replay.Duplicate);
        Assert.Equal("Draining", replay.Lifecycle);
    }

    [Fact]
    public async Task DrainProcessRefusesAnUnknownProcess()
    {
        await using var rig = await Rig.CreateAsync();
        await Rejects(() => rig.Service.DrainProcess(new AdminDrainProcessRequest
        {
            RequestId = "drain-2", Operator = "gm-1", Reason = "noisy neighbor",
            ProcessInstanceId = "proc-none"
        }, rig.Operator("token-gm")),
            StatusCode.NotFound, "The process 'proc-none' is not registered.");
    }

    [Fact]
    public async Task DrainProcessRefusesAnIncompleteCommand()
    {
        await using var rig = await Rig.CreateAsync();
        await Rejects(() => rig.Service.DrainProcess(new AdminDrainProcessRequest
        {
            RequestId = "drain-3", Operator = "gm-1", Reason = ""
        }, rig.Operator("token-gm")),
            StatusCode.InvalidArgument, "The drain request is incomplete.");
    }

    [Fact]
    public async Task RetryRoomRecoveryDrivesTheOneBarrieredFlow()
    {
        await using var rig = await Rig.CreateAsync();
        await rig.SeedOwnershipAsync("room-rec-1", "gs-2", epoch: 1);
        // Past the operator gate and the audit, the recovery is the takeover flow: the
        // target host answers on its dead endpoint — never a second implementation.
        var error = await Assert.ThrowsAsync<RpcException>(() => rig.Service.RetryRoomRecovery(
            new AdminRoomRecoveryRequest
            {
                RequestId = "rec-1", Operator = "gm-1", Reason = "owner silent",
                TenantId = "tenant-a", GameId = "game-a", RoomId = "room-rec-1",
                ToInstanceId = "gs-2", TickMs = 25
            }, rig.Operator("token-gm")));
        Assert.Equal(StatusCode.Unavailable, error.Status.StatusCode);
        var replay = await rig.Service.RetryRoomRecovery(new AdminRoomRecoveryRequest
        {
            RequestId = "rec-1", Operator = "gm-1", Reason = "owner silent",
            TenantId = "tenant-a", GameId = "game-a", RoomId = "room-rec-1",
            ToInstanceId = "gs-2", TickMs = 25
        }, rig.Operator("token-gm"));
        Assert.True(replay.Duplicate);
    }

    [Fact]
    public async Task RetryRoomRecoveryRefusesAnIncompleteCommand()
    {
        await using var rig = await Rig.CreateAsync();
        await Rejects(() => rig.Service.RetryRoomRecovery(new AdminRoomRecoveryRequest
        {
            RequestId = "rec-2", Operator = "gm-1", Reason = "owner silent",
            TenantId = "tenant-a", GameId = "game-a", RoomId = "room-rec-2",
            ToInstanceId = "gs-2", TickMs = 0
        }, rig.Operator("token-gm")),
            StatusCode.InvalidArgument, "The recovery request is incomplete.");
    }

    [Fact]
    public async Task RetrySettlementReachesTheExecutorPastTheGates()
    {
        await using var rig = await Rig.CreateAsync();
        await rig.SeedRoomRowAsync("room-settle-1");
        // The room exists, the operator is authorized and audited; the settlement commit is
        // the data plane's transaction, so the dead endpoint answers.
        var error = await Assert.ThrowsAsync<RpcException>(() => rig.Service.RetrySettlement(
            new AdminSettlementRetryRequest
            {
                RequestId = "settle-1", Operator = "gm-1", Reason = "ledger line missing",
                RoomId = "room-settle-1"
            }, rig.Operator("token-gm")));
        Assert.Equal(StatusCode.Unavailable, error.Status.StatusCode);
        // A replay of the same request id answers as a visible duplicate without reaching
        // the executor again.
        var replay = await rig.Service.RetrySettlement(new AdminSettlementRetryRequest
        {
            RequestId = "settle-1", Operator = "gm-1", Reason = "ledger line missing",
            RoomId = "room-settle-1"
        }, rig.Operator("token-gm"));
        Assert.True(replay.Duplicate);
        Assert.Equal("room-settle-1", replay.RoomId);
    }

    [Fact]
    public async Task RetrySettlementRefusesAnUnknownRoom()
    {
        await using var rig = await Rig.CreateAsync();
        await Rejects(() => rig.Service.RetrySettlement(new AdminSettlementRetryRequest
        {
            RequestId = "settle-2", Operator = "gm-1", Reason = "ledger line missing",
            RoomId = "room-none"
        }, rig.Operator("token-gm")),
            StatusCode.NotFound, "The room does not exist.");
    }

    [Fact]
    public async Task ReconcilePlatformTaskReachesTheTaskQueuePastTheGates()
    {
        await using var rig = await Rig.CreateAsync();
        var error = await Assert.ThrowsAsync<RpcException>(() => rig.Service.ReconcilePlatformTask(
            new AdminTaskReconcileRequest
            {
                RequestId = "task-1", Operator = "gm-1", Reason = "stuck attempt",
                TaskId = "task-1"
            }, rig.Operator("token-gm")));
        Assert.Equal(StatusCode.Unavailable, error.Status.StatusCode);
        var replay = await rig.Service.ReconcilePlatformTask(new AdminTaskReconcileRequest
        {
            RequestId = "task-1", Operator = "gm-1", Reason = "stuck attempt", TaskId = "task-1"
        }, rig.Operator("token-gm"));
        Assert.True(replay.Duplicate);
    }

    [Fact]
    public async Task ReconcilePlatformTaskRefusesAnIncompleteCommand()
    {
        await using var rig = await Rig.CreateAsync();
        await Rejects(() => rig.Service.ReconcilePlatformTask(new AdminTaskReconcileRequest
        {
            RequestId = "task-2", Operator = "gm-1", Reason = "stuck attempt", TaskId = ""
        }, rig.Operator("token-gm")),
            StatusCode.InvalidArgument, "The reconcile request is incomplete.");
    }

    [Fact]
    public async Task GetOperationStatusReachesTheDataPlanePastTheGates()
    {
        await using var rig = await Rig.CreateAsync();
        // The status query is the viewer-floored read: the viewer token carries no tenant or
        // game scope, which is the "no constraint" case, so the query passes the fence and
        // asks the data plane for the input row — the dead endpoint answers.
        await rig.AuditAsync("task-3", "gm-1", "stuck attempt", "task-x", "ReconcilePlatformTask",
            """{"task":"task-x"}""");
        var error = await Assert.ThrowsAsync<RpcException>(() => rig.Service.GetOperationStatus(
            new AdminGetOperationStatusRequest { OperationId = "task-3" }, rig.Operator("token-viewer")));
        Assert.Equal(StatusCode.Unavailable, error.Status.StatusCode);
    }

    [Fact]
    public async Task GetOperationStatusRefusesAMissingOperationId()
    {
        await using var rig = await Rig.CreateAsync();
        await Rejects(() => rig.Service.GetOperationStatus(new AdminGetOperationStatusRequest
        {
            OperationId = ""
        }, rig.Operator("token-viewer")),
            StatusCode.InvalidArgument, "The status query needs an operation id.");
    }

    [Fact]
    public async Task TheAuditTrailPagesNewestFirstWithExactWordFilters()
    {
        await using var rig = await Rig.CreateAsync();
        await rig.AuditAsync("a-1", "gm-1", "first", "room-r", "RetrySettlement", "{}");
        await rig.AuditAsync("a-2", "gm-1", "second", "room-r", "DrainProcess", "{}");
        await rig.AuditAsync("a-3", "viewer-1", "third", "room-r", "RetrySettlement", "{}");
        var reply = await rig.Service.GetAuditTrail(new AdminAuditTrailRequest
        {
            MaxEntries = 2
        }, rig.Operator("token-viewer"));
        Assert.Equal(2, reply.Entries.Count);
        Assert.True(reply.HasMore);
        // Newest first on the append-only log, so the last row written leads.
        Assert.Equal("a-3", reply.Entries[0].RequestId);
        Assert.Equal("a-2", reply.Entries[1].RequestId);
        // The cursor continues the walk on the keyset: everything strictly older than the
        // page's last row.
        var rest = await rig.Service.GetAuditTrail(new AdminAuditTrailRequest
        {
            MaxEntries = 2, PageToken = reply.NextPageToken
        }, rig.Operator("token-viewer"));
        Assert.False(rest.HasMore);
        Assert.Equal(1, rest.Entries.Count);
        Assert.Equal("a-1", rest.Entries[0].RequestId);
        // Exact-word filters narrow to the command, not a pattern.
        var filtered = await rig.Service.GetAuditTrail(new AdminAuditTrailRequest
        {
            Command = "RetrySettlement", MaxEntries = 10
        }, rig.Operator("token-viewer"));
        Assert.Equal(2, filtered.Entries.Count);
        Assert.All(filtered.Entries, entry => Assert.Equal("RetrySettlement", entry.Command));
    }

    [Fact]
    public async Task TheAuditTrailRefusesAnUnboundedPage()
    {
        await using var rig = await Rig.CreateAsync();
        await Rejects(() => rig.Service.GetAuditTrail(new AdminAuditTrailRequest
        {
            MaxEntries = 0
        }, rig.Operator("token-viewer")),
            StatusCode.InvalidArgument, "The audit trail page is bounded (1..200).");
        await Rejects(() => rig.Service.GetAuditTrail(new AdminAuditTrailRequest
        {
            MaxEntries = 201
        }, rig.Operator("token-viewer")),
            StatusCode.InvalidArgument, "The audit trail page is bounded (1..200).");
    }

    [Fact]
    public async Task TheStreamerSnapshotAssemblesTheFootprint()
    {
        await using var rig = await Rig.CreateAsync();
        var (streamerId, bindingId) = await rig.SeedStreamerFootprintAsync();
        var reply = await rig.Service.GetStreamerSnapshot(new AdminStreamerSnapshotRequest
        {
            TenantId = "tenant-a", StreamerId = streamerId
        }, rig.Operator("token-viewer"));
        Assert.True(reply.Found);
        Assert.Equal(streamerId, reply.StreamerId);
        var binding = Assert.Single(reply.Bindings);
        Assert.Equal(bindingId, binding.BindingId);
        Assert.Equal("game-a", binding.GameId);
        Assert.True(reply.OwnershipPresent);
        Assert.Equal("streamer-a", reply.OwnerInstanceId);
        Assert.Equal(1, reply.OwnershipEpoch);
        Assert.Equal("Active", reply.OwnershipStatus);
    }

    [Fact]
    public async Task TheStreamerSnapshotRefusesAnIncompleteRequest()
    {
        await using var rig = await Rig.CreateAsync();
        await Rejects(() => rig.Service.GetStreamerSnapshot(new AdminStreamerSnapshotRequest
        {
            TenantId = "tenant-a", StreamerId = ""
        }, rig.Operator("token-viewer")),
            StatusCode.InvalidArgument, "The snapshot request is incomplete.");
    }

    [Fact]
    public async Task TheRoomSnapshotAssemblesTheRoomFootprint()
    {
        await using var rig = await Rig.CreateAsync();
        const string room = "room-snap-1";
        await rig.SeedRoomFootprintAsync(room);
        var reply = await rig.Service.GetRoomSnapshot(new AdminRoomSnapshotRequest { RoomId = room },
            rig.Operator("token-viewer"));
        Assert.True(reply.Found);
        Assert.Equal("tenant-a", reply.TenantId);
        Assert.Equal("game-a", reply.GameId);
        Assert.Equal("Finishing", reply.Status);
        Assert.True(reply.OwnershipPresent);
        Assert.Equal("gs-1", reply.OwnerInstanceId);
        Assert.Equal(2, reply.Participants.Count);
        Assert.Equal(1, reply.ResultVersion);
        Assert.Equal(2, reply.GrantsTotal);
        Assert.Equal(1, reply.GrantsGranted);
    }

    [Fact]
    public async Task TheRoomSnapshotRefusesAnIncompleteRequest()
    {
        await using var rig = await Rig.CreateAsync();
        await Rejects(() => rig.Service.GetRoomSnapshot(new AdminRoomSnapshotRequest { RoomId = "" },
                rig.Operator("token-viewer")),
            StatusCode.InvalidArgument, "The snapshot request is incomplete.");
    }

    [Fact]
    public async Task TheRoomSnapshotRefusesAScopeOutsideTheOperator()
    {
        await using var rig = await Rig.CreateAsync();
        const string room = "room-snap-2";
        await rig.SeedRoomFootprintAsync(room);
        await Rejects(() => rig.Service.GetRoomSnapshot(new AdminRoomSnapshotRequest { RoomId = room },
                rig.Operator("token-gm-b")),
            StatusCode.PermissionDenied, "Operator scope does not cover the target room.");
    }

    [Fact]
    public async Task TraceRoundWalksTheWholeCausalChain()
    {
        await using var rig = await Rig.CreateAsync();
        const string room = "room-trace-1";
        await rig.SeedRoomFootprintAsync(room);
        var reply = await rig.Service.TraceRound(new AdminTraceRoundRequest { RoomId = room },
            rig.Operator("token-viewer"));
        Assert.True(reply.Found);
        Assert.Equal("Finishing", reply.Status);
        Assert.Equal(2, reply.Events.Count);
        Assert.Equal(2, reply.Frames.Count);
        Assert.Contains("ev-1", reply.Frames[0].InputRefsJson);
        Assert.Equal(2, reply.Effects.Count);
        Assert.Equal(1, reply.ResultVersion);
        Assert.Equal(2, reply.Grants.Count);
        Assert.Single(reply.Ledger);
    }

    [Fact]
    public async Task TraceRoundRefusesAnIncompleteRequest()
    {
        await using var rig = await Rig.CreateAsync();
        await Rejects(() => rig.Service.TraceRound(new AdminTraceRoundRequest { RoomId = "" },
                rig.Operator("token-viewer")),
            StatusCode.InvalidArgument, "The trace request is incomplete.");
    }

    [Fact]
    public async Task TheLeaderboardTopPageAnswersInTheBoardOrder()
    {
        await using var rig = await Rig.CreateAsync();
        rig.Leaderboard.Answer(
            new TopNReply
            {
                ReadVersion = "2026-11-12T10:00:00.0000000+00:00",
                Entries = { new RankEntryProto { Subject = 0, SubjectId = "p-1", Score = 30 },
                    new RankEntryProto { Subject = 0, SubjectId = "p-2", Score = 20 } },
                NextCursor = "cursor-2"
            });
        var reply = await rig.Service.GetLeaderboardTop(new AdminLeaderboardTopRequest
        {
            TenantId = "tenant-a", GameId = "game-a", Board = "score", MaxEntries = 2
        }, rig.Operator("token-viewer"));
        Assert.Equal(2, reply.Entries.Count);
        Assert.Equal("p-1", reply.Entries[0].SubjectId);
        Assert.Equal(30, reply.Entries[0].Score);
        Assert.True(reply.HasMore);
        Assert.Equal("cursor-2", reply.NextPageToken);
        Assert.True(reply.ReadVersionUnixMs > 0);
        Assert.Contains("2026-11-12T10:00:00", reply.ReadVersion);
    }

    [Fact]
    public async Task TheLeaderboardRankAnswersOneSubject()
    {
        await using var rig = await Rig.CreateAsync();
        rig.Leaderboard.Answer(new RankReply
        {
            Found = true, Rank = 2, ReadVersion = "2026-11-12T10:00:00.0000000+00:00",
            Entry = new RankEntryProto { Subject = 0, SubjectId = "p-2", Score = 20 }
        });
        var reply = await rig.Service.GetLeaderboardRank(new AdminLeaderboardRankRequest
        {
            TenantId = "tenant-a", GameId = "game-a", Board = "score", SubjectId = "p-2"
        }, rig.Operator("token-viewer"));
        Assert.True(reply.Found);
        Assert.Equal(2, reply.Rank);
        Assert.Equal(20, reply.Score);
    }

    [Fact]
    public async Task TheLeaderboardAroundWindowAnswersTheNeighbours()
    {
        await using var rig = await Rig.CreateAsync();
        rig.Leaderboard.Answer(new AroundReply
        {
            Found = true, ReadVersion = "2026-11-12T10:00:00.0000000+00:00",
            Entries =
            {
                new RankedEntryProto { Rank = 1, Entry = new RankEntryProto { Subject = 0, SubjectId = "p-1", Score = 30 } },
                new RankedEntryProto { Rank = 2, Entry = new RankEntryProto { Subject = 0, SubjectId = "p-2", Score = 20 } }
            }
        });
        var reply = await rig.Service.GetLeaderboardAround(new AdminLeaderboardAroundRequest
        {
            TenantId = "tenant-a", GameId = "game-a", Board = "score", SubjectId = "p-2", Window = 1
        }, rig.Operator("token-viewer"));
        Assert.True(reply.Found);
        Assert.Equal(2, reply.Entries.Count);
        Assert.Equal(2, reply.Entries[1].Rank);
        Assert.Equal("p-2", reply.Entries[1].SubjectId);
    }

    [Fact]
    public async Task TheLeaderboardQueriesRefuseAnIncompleteRequest()
    {
        await using var rig = await Rig.CreateAsync();
        await Rejects(() => rig.Service.GetLeaderboardTop(new AdminLeaderboardTopRequest
        {
            TenantId = "tenant-a", GameId = "game-a", Board = "", MaxEntries = 10
        }, rig.Operator("token-viewer")),
            StatusCode.InvalidArgument, "The leaderboard request is incomplete.");
        await Rejects(() => rig.Service.GetLeaderboardRank(new AdminLeaderboardRankRequest
        {
            TenantId = "tenant-a", GameId = "game-a", Board = "score", SubjectId = ""
        }, rig.Operator("token-viewer")),
            StatusCode.InvalidArgument, "The leaderboard request is incomplete.");
        await Rejects(() => rig.Service.GetLeaderboardAround(new AdminLeaderboardAroundRequest
        {
            TenantId = "tenant-a", GameId = "game-a", Board = "score", SubjectId = ""
        }, rig.Operator("token-viewer")),
            StatusCode.InvalidArgument, "The leaderboard request is incomplete.");
    }

    [Fact]
    public async Task TheLeaderboardQueriesMapTheDataPlaneRefusals()
    {
        await using var rig = await Rig.CreateAsync();
        // A stale continuation (FailedPrecondition from the data plane) is an explicit
        // precondition error — never a cross-version page.
        rig.Leaderboard.Fail(new RpcException(new Status(StatusCode.FailedPrecondition,
            "The snapshot walk expired.")));
        await Rejects(() => rig.Service.GetLeaderboardTop(new AdminLeaderboardTopRequest
        {
            TenantId = "tenant-a", GameId = "game-a", Board = "score", MaxEntries = 10
        }, rig.Operator("token-viewer")),
            StatusCode.FailedPrecondition, "The snapshot walk expired.");
        // A malformed board from the data plane (InvalidArgument) surfaces as an invalid
        // request — the face's own completeness check has already passed, so the word is
        // the data plane's.
        rig.Leaderboard.Fail(new RpcException(new Status(StatusCode.InvalidArgument,
            "A tenant id must not be empty.")));
        await Rejects(() => rig.Service.GetLeaderboardTop(new AdminLeaderboardTopRequest
        {
            TenantId = "tenant-a", GameId = "game-a", Board = "score", MaxEntries = 10
        }, rig.Operator("token-viewer")),
            StatusCode.InvalidArgument, "A tenant id must not be empty.");
    }

    [Fact]
    public async Task TheLeaderboardCorrectionAppliesOnceAndReportsTheVersion()
    {
        await using var rig = await Rig.CreateAsync();
        rig.Leaderboard.Answer(new AdjustBoardReply
        {
            Score = 35, Duplicate = false, Version = "2026-11-12T11:00:00.0000000+00:00"
        });
        var reply = await rig.Service.AdjustLeaderboardEntry(new AdminLeaderboardAdjustRequest
        {
            RequestId = "adj-1", Operator = "gm-1", Reason = "score dispute",
            TenantId = "tenant-a", GameId = "game-a", Board = "score", SubjectId = "p-1",
            Delta = 5, ExpectedVersion = "2026-11-12T10:00:00.0000000+00:00"
        }, rig.Operator("token-gm"));
        Assert.Equal(35, reply.Score);
        Assert.False(reply.Duplicate);
        Assert.Contains("2026-11-12T11:00:00", reply.ReadVersion);
    }

    [Fact]
    public async Task TheLeaderboardCorrectionRefusesAForgedOperatorAndIncompleteRequests()
    {
        await using var rig = await Rig.CreateAsync();
        await Rejects(() => rig.Service.AdjustLeaderboardEntry(new AdminLeaderboardAdjustRequest
        {
            RequestId = "adj-2", Operator = "someone-else", Reason = "score dispute",
            TenantId = "tenant-a", GameId = "game-a", Board = "score", SubjectId = "p-1", Delta = 5
        }, rig.Operator("token-gm")),
            StatusCode.PermissionDenied, "The request operator does not match the authenticated operator.");
        await Rejects(() => rig.Service.AdjustLeaderboardEntry(new AdminLeaderboardAdjustRequest
        {
            RequestId = "", Operator = "gm-1", Reason = "score dispute",
            TenantId = "tenant-a", GameId = "game-a", Board = "score", SubjectId = "p-1", Delta = 5
        }, rig.Operator("token-gm")),
            StatusCode.InvalidArgument, "The leaderboard adjustment request is incomplete.");
    }

    [Fact]
    public async Task TheLeaderboardCorrectionMapsTheRefusals()
    {
        await using var rig = await Rig.CreateAsync();
        // A stale read version (Aborted from the data plane) is a precondition failure.
        rig.Leaderboard.Fail(new RpcException(new Status(StatusCode.Aborted, "The board moved on.")));
        await Rejects(() => rig.Service.AdjustLeaderboardEntry(new AdminLeaderboardAdjustRequest
        {
            RequestId = "adj-3", Operator = "gm-1", Reason = "score dispute",
            TenantId = "tenant-a", GameId = "game-a", Board = "score", SubjectId = "p-1", Delta = 5
        }, rig.Operator("token-gm")),
            StatusCode.FailedPrecondition, "The board moved on.");
        // A spent operation id (AlreadyExists) is a visible conflict.
        rig.Leaderboard.Fail(new RpcException(new Status(StatusCode.AlreadyExists,
            "The operation id is spent.")));
        await Rejects(() => rig.Service.AdjustLeaderboardEntry(new AdminLeaderboardAdjustRequest
        {
            RequestId = "adj-4", Operator = "gm-1", Reason = "score dispute",
            TenantId = "tenant-a", GameId = "game-a", Board = "score", SubjectId = "p-1", Delta = 5
        }, rig.Operator("token-gm")),
            StatusCode.AlreadyExists, "The operation id is spent.");
    }

    [Fact]
    public async Task TheLeaderboardResetAnswersTheBoardAndWholeBoardVariants()
    {
        await using var rig = await Rig.CreateAsync();
        rig.Leaderboard.Answer(new ResetBoardReply
        {
            Duplicate = false, Version = "2026-11-12T12:00:00.0000000+00:00"
        });
        var reply = await rig.Service.ResetLeaderboardEntry(new AdminLeaderboardResetRequest
        {
            RequestId = "reset-1", Operator = "gm-1", Reason = "season end",
            TenantId = "tenant-a", GameId = "game-a", Board = "score"
        }, rig.Operator("token-gm"));
        Assert.False(reply.Duplicate);
        Assert.Contains("2026-11-12T12:00:00", reply.ReadVersion);
        await Rejects(() => rig.Service.ResetLeaderboardEntry(new AdminLeaderboardResetRequest
        {
            RequestId = "reset-2", Operator = "gm-1", Reason = "",
            TenantId = "tenant-a", GameId = "game-a", Board = "score"
        }, rig.Operator("token-gm")),
            StatusCode.InvalidArgument, "The leaderboard reset request is incomplete.");
    }

    [Fact]
    public async Task TheLeaderboardRebuildAnswersTheReDerivedCount()
    {
        await using var rig = await Rig.CreateAsync();
        rig.Leaderboard.Answer(new RebuildBoardReply
        {
            Entries = 7, Version = "2026-11-12T13:00:00.0000000+00:00"
        });
        var reply = await rig.Service.RebuildLeaderboard(new AdminLeaderboardRebuildRequest
        {
            RequestId = "rebuild-1", Operator = "gm-1", Reason = "projection in doubt",
            TenantId = "tenant-a", GameId = "game-a", Board = "score"
        }, rig.Operator("token-gm"));
        Assert.Equal(7, reply.Entries);
        await Rejects(() => rig.Service.RebuildLeaderboard(new AdminLeaderboardRebuildRequest
        {
            RequestId = "rebuild-2", Operator = "gm-1", Reason = "projection in doubt",
            TenantId = "tenant-a", GameId = "game-a", Board = ""
        }, rig.Operator("token-gm")),
            StatusCode.InvalidArgument, "The leaderboard rebuild request is incomplete.");
    }

    private static AdminQueueGateRequest AdminQueueGate(string partition, string requestId,
        string operatorName = "gm-1") => new()
    {
        RequestId = requestId, Operator = operatorName, Reason = "load control",
        PartitionKey = partition
    };

    private static async Task Rejects(Func<Task> call, StatusCode code, string detail)
    {
        var error = await Assert.ThrowsAsync<RpcException>(call);
        Assert.Equal(code, error.Status.StatusCode);
        if (detail is not null) Assert.Equal(detail, error.Status.Detail);
    }

    /// <summary>One admin face over one temporary database: a dead data plane and a dead
    /// game host named in the directory, real stores, and a scripted leaderboard data
    /// plane.</summary>
    private sealed class Rig : IAsyncDisposable
    {
        private readonly TemporaryDatabase _database;
        public TestProcessCertificates Certificates { get; } = new();
        public FakeLeaderboardDataPlane Leaderboard { get; } = new();
        public RedesignAdminStore AdminStore { get; private set; } = null!;
        public AdminGrpcService Service { get; private set; } = null!;

        private Rig(TemporaryDatabase database) => _database = database;

        public static async Task<Rig> CreateAsync()
        {
            var source = Environment.GetEnvironmentVariable("CHORUS_TEST_POSTGRES")
                ?? throw new InvalidOperationException(
                    "Set CHORUS_TEST_POSTGRES to a PostgreSQL role with CREATEDB.");
            var database = await TemporaryDatabase.CreateAsync(source);
            try
            {
                var rig = new Rig(database);
                rig.AdminStore = new RedesignAdminStore(database.ConnectionString);
                var controlStore = new RedesignControlStore(database.ConnectionString);
                var operators = new RedesignOperatorStore(database.ConnectionString);
                await operators.RegisterOperatorAsync("gm-1", "gm", ["tenant-a"], ["game-a"], "token-gm");
                await operators.RegisterOperatorAsync("viewer-1", "viewer", [], [], "token-viewer");
                await operators.RegisterOperatorAsync("gm-b", "gm", ["tenant-b"], ["game-b"], "token-gm-b");
                var directory = new ClusterDirectory(
                    rig.Certificates.Issue("@cluster", "control-server", "control-1"),
                    rig.Certificates.Root, rig.Certificates.NodeAuthority, "cluster-a", DeadEndpoint,
                    ["gs-2=https://127.0.0.1:9"]);
                var queries = new LeaderboardQueryService(rig.Leaderboard);
                rig.Service = new AdminGrpcService(directory, rig.AdminStore, controlStore,
                    operators, rig.Certificates.Trusted, queries);
                return rig;
            }
            catch
            {
                await database.DisposeAsync();
                throw;
            }
        }

        public TestServerCallContext Context(string tenant, string role, string instance) =>
            Certificates.Context(Certificates.Issue(tenant, role, instance));

        /// <summary>The operator's call context: the bearer credential rides the standard
        /// Authorization header the admin face reads.</summary>
        public TestServerCallContext Operator(string token)
        {
            var context = Certificates.Context(null);
            context.AddHeader("authorization", "Bearer " + token);
            return context;
        }

        public Task AuditAsync(string requestId, string operation, string reason, string scope,
            string command, string detailJson) =>
            AdminStore.WriteAuditAsync(requestId, operation, reason, scope, command, detailJson);

        /// <summary>Seeds an ownership row directly — the epoch entries read the row's owner,
        /// epoch and status, which is all these facts need to hold.</summary>
        public Task SeedOwnershipAsync(string roomId, string ownerInstanceId, long epoch = 1) =>
            SeedAsync("""
                INSERT INTO control.ownership(tenant_id,game_id,kind,entity_id,owner_process_instance_id,
                    epoch,lease_until,status,version)
                VALUES ('tenant-a','game-a','room',@room,@owner,@epoch,
                    clock_timestamp()+interval '60 second','Active',1)
                """, ("room", roomId), ("owner", ownerInstanceId), ("epoch", epoch));

        /// <summary>Seeds the epoch bump's ground truth: an Active ownership and its agreeing
        /// game.room row at the same epoch (the FIX-GM-02 storage transaction).</summary>
        public Task SeedRunningRoomAsync(string roomId, long epoch) =>
            SeedAsync("""
                INSERT INTO control.ownership(tenant_id,game_id,kind,entity_id,owner_process_instance_id,
                    epoch,lease_until,status,version)
                VALUES ('tenant-a','game-a','room',@room,'gs-1',@epoch,
                    clock_timestamp()+interval '60 second','Active',1);
                INSERT INTO game.room(room_id,tenant_id,game_id,status,epoch,package_hash,module_version,
                    config_hash)
                VALUES (@room,'tenant-a','game-a','Running',@epoch,'pkg','1.0.0','cfg');
                """, ("room", roomId), ("epoch", epoch));

        /// <summary>Seeds a bare game.room row — the settlement retry resolves the room's own
        /// tenant/game scope from it.</summary>
        public Task SeedRoomRowAsync(string roomId) =>
            SeedAsync("""
                INSERT INTO game.room(room_id,tenant_id,game_id,status,epoch,package_hash,module_version,
                    config_hash)
                VALUES (@room,'tenant-a','game-a','Finishing',1,'pkg','1.0.0','cfg')
                """, ("room", roomId));

        /// <summary>Seeds a process row through the node's agent the way a live report does
        /// (the node row comes first — the report locks it).</summary>
        public async Task SeedProcessAsync(string processInstanceId)
        {
            var control = new RedesignControlStore(_database.ConnectionString);
            await control.RegisterNodeAsync("node-a", "agent-1", "{}");
            await control.ReportProcessAsync("node-a", "agent-1", "node-a/game-server", "game-server",
                processInstanceId, 1, "Running", "Ready", "Healthy", "Accepting", "Steady", DateTime.UtcNow);
        }

        /// <summary>Seeds the streamer footprint the snapshot assembles: a mount with a live
        /// room and an Active ownership.</summary>
        public async Task<(string StreamerId, string BindingId)> SeedStreamerFootprintAsync()
        {
            var identity = new RedesignIdentityStore(_database.ConnectionString);
            var bindings = new RedesignBindingStore(_database.ConnectionString);
            var streamer = await identity.ResolveIdentityAsync("tenant-a", "simulator", "app-1",
                "admin-face-open");
            var live = await bindings.OpenLiveSessionAsync("tenant-a", streamer.StreamerId, "simulator",
                "app-1", "platform-room", "live-admin-face");
            var session = await identity.LoginAsync(streamer.StreamerId, "gateway-1", "connection-1",
                RandomNumberGenerator.GetBytes(32));
            var mount = await bindings.StartGameplayAsync("tenant-a", live, session.SessionId, "game-a",
                "mount-admin-face");
            await SeedOwnershipAsync("room-admin-face", "streamer-a");
            await SeedAsync("""
                UPDATE live.binding SET current_room_id='room-admin-face' WHERE binding_id=@binding
                """, ("binding", mount.BindingId));
            await SeedAsync("""
                INSERT INTO control.ownership(tenant_id,game_id,kind,entity_id,owner_process_instance_id,
                    epoch,lease_until,status,version)
                VALUES ('tenant-a','game-a','streamer',@streamer,'streamer-a',1,
                    clock_timestamp()+interval '60 second','Active',1)
                """, ("streamer", streamer.StreamerId));
            return (streamer.StreamerId, mount.BindingId);
        }

        /// <summary>Seeds the room footprint the snapshot and trace assemble (the AT31 causal
        /// chain: events, frames, effects, result, grants, ledger).</summary>
        public Task SeedRoomFootprintAsync(string room) =>
            SeedAsync("""
                INSERT INTO game.room(room_id,tenant_id,game_id,status,epoch,package_hash,module_version,
                    config_hash)
                VALUES (@room,'tenant-a','game-a','Finishing',1,'pkg','1.0.0','cfg');
                INSERT INTO game.participation(room_id,participant_id,binding_id,role,state)
                VALUES (@room,'p-host',NULL,'Host','Active'), (@room,'p-guest',NULL,'Guest','Active');
                INSERT INTO input.event(event_id,platform,app_id,source_dedup_key,payload_hash,
                    disposition,payload)
                VALUES ('ev-1','simulator','app-1','gift-1',@hash,'Consumed','{"kind":"gift","amount":100}'),
                       ('ev-2','simulator','app-1','gift-2',@hash,'Consumed','{"kind":"gift","amount":50}');
                INSERT INTO input.cursor(consumer_scope) VALUES ('room:' || @room);
                INSERT INTO input.delivery(event_id,consumer_scope,room_id,route_version,sequence,state)
                VALUES ('ev-1','room:' || @room,@room,1,1,'Consumed'),
                       ('ev-2','room:' || @room,@room,1,2,'Consumed');
                INSERT INTO game.frame(room_id,tick_id,commit_id,input_refs,state_hash,effects,
                    module_version,config_hash)
                VALUES (@room,1,'commit-1','["ev-1","ev-2"]',@hash,'[{"kind":"score"}]','1.0.0','cfg'),
                       (@room,2,'commit-2','[]',@hash,'[]','1.0.0','cfg');
                INSERT INTO infra.outbox(effect_id,kind,payload,status)
                VALUES (@room || '/1/0','score','{"player":"p-1"}','Recorded'),
                       (@room || '/2/0','round.finished','{}','Pending');
                INSERT INTO game.result(room_id,result_version,final_frame,plan_hash,result)
                VALUES (@room,1,2,@hash,'{"winner":"p-1"}');
                INSERT INTO settlement.grant(room_id,result_version,player_id,grant_key,status,
                    operation_id,asset_id,delta)
                VALUES (@room,1,'p-1','win','Granted','grant-op-1','score',10),
                       (@room,1,'p-1','mvp','Planned','grant-op-2','score',5);
                INSERT INTO player.ledger(tenant_id,game_id,operation_id,line_id,player_id,asset_id,delta,
                    reason,resulting_balance)
                VALUES ('tenant-a','game-a','grant-op-1','grant','p-1','score',10,
                    'settlement:' || @room || '/win',10);
                INSERT INTO control.ownership(tenant_id,game_id,kind,entity_id,owner_process_instance_id,
                    epoch,lease_until,status,version)
                VALUES ('tenant-a','game-a','room',@room,'gs-1',1,clock_timestamp()+interval '10 s',
                    'Active',1);
                """, ("room", room), ("hash", RandomNumberGenerator.GetBytes(4)));

        private async Task SeedAsync(string sql, params (string Name, object Value)[] parameters)
        {
            await using var connection = new NpgsqlConnection(_database.ConnectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(sql, connection);
            foreach (var (name, value) in parameters)
                command.Parameters.AddWithValue(name, value);
            await command.ExecuteNonQueryAsync();
        }

        public async ValueTask DisposeAsync() => await _database.DisposeAsync();
    }

    /// <summary>The leaderboard data plane the scripted facts answer with: one queued reply
    /// per call, so the query module's board validation and error mapping are driven for
    /// real against answers the tests chose.</summary>
    private sealed class FakeLeaderboardDataPlane : ILeaderboardQueryDataPlane
    {
        private readonly Queue<object> _answers = [];

        public void Answer(object reply) => _answers.Enqueue(reply);

        public void Fail(RpcException error) => _answers.Enqueue(error);

        private T Next<T>()
        {
            if (_answers.Count == 0)
                throw new InvalidOperationException("The scripted data plane has no answer queued.");
            return _answers.Dequeue() switch
            {
                T reply => reply,
                RpcException error => throw error,
                var other => throw new InvalidOperationException(
                    $"The scripted answer {other?.GetType().Name} does not answer this call.")
            };
        }

        public Task<TopNReply> GetTopAsync(GetTopRequest request,
            CancellationToken cancellationToken = default) => Task.FromResult(Next<TopNReply>());

        public Task<RankReply> GetRankAsync(GetRankRequest request,
            CancellationToken cancellationToken = default) => Task.FromResult(Next<RankReply>());

        public Task<AroundReply> GetAroundMeAsync(GetAroundMeRequest request,
            CancellationToken cancellationToken = default) => Task.FromResult(Next<AroundReply>());

        public Task<AdjustBoardReply> AdjustBoardAsync(AdjustBoardRequest request,
            CancellationToken cancellationToken = default) => Task.FromResult(Next<AdjustBoardReply>());

        public Task<ResetBoardReply> ResetBoardAsync(ResetBoardRequest request,
            CancellationToken cancellationToken = default) => Task.FromResult(Next<ResetBoardReply>());

        public Task<RebuildBoardReply> RebuildBoardAsync(RebuildBoardRequest request,
            CancellationToken cancellationToken = default) => Task.FromResult(Next<RebuildBoardReply>());
    }

    private sealed class TemporaryDatabase(string source) : IAsyncDisposable
    {
        private readonly string _name = "chorus_admin_face_" + Guid.NewGuid().ToString("N");
        private readonly NpgsqlConnectionStringBuilder _admin = new(source) { Pooling = false };
        public string ConnectionString { get; private set; } = "";

        public static async Task<TemporaryDatabase> CreateAsync(string source)
        {
            var database = new TemporaryDatabase(source);
            await database.InitializeAsync();
            return database;
        }

        private async Task InitializeAsync()
        {
            await using var admin = new NpgsqlConnection(_admin.ConnectionString);
            await admin.OpenAsync();
            await using (var create = new NpgsqlCommand($"CREATE DATABASE \"{_name}\"", admin))
                await create.ExecuteNonQueryAsync();
            var target = new NpgsqlConnectionStringBuilder(_admin.ConnectionString) { Database = _name };
            ConnectionString = target.ConnectionString;
            await using var connection = new NpgsqlConnection(ConnectionString);
            await connection.OpenAsync();
            await RedesignSchemaMigrator.ApplyAsync(connection);
        }

        public async ValueTask DisposeAsync()
        {
            await using var admin = new NpgsqlConnection(_admin.ConnectionString);
            await admin.OpenAsync();
            await TestDatabaseDrop.DropAsync(admin, _name);
        }
    }
}

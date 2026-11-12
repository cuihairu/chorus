using System.Security.Cryptography;
using System.Text;
using Chorus.ControlServer;
using Chorus.Data;
using Chorus.DataServer;
using Chorus.Rpc;
using Grpc.Core;
using Npgsql;

namespace Chorus.Engine.Tests;

/// <summary>
/// Face of the settlement and round finalization service (architecture 9.3): the manifest
/// settles once through receipt-identified grant transactions, the terminator only releases
/// rooms whose evidence chain is complete — committed result, settled manifest, disposed
/// inputs and the end ladder's platform-close phases — and the settle queue pages exactly
/// the rooms a recoverer must still pay. The tenant fence and the role gates ride every call.
/// </summary>
public sealed class RedesignSettlementGrpcFaceTests
{
    private const string Tenant = "tenant-a";
    private const string Game = "game-a";
    private const string Instance = "settle-1";

    private static CommitResultRequest Commit(string roomId, string operationId = "settle-op-1",
        string tenantId = Tenant, string gameId = Game) => new()
    {
        RoomId = roomId, GameId = gameId, OperationId = operationId, TenantId = tenantId
    };

    private static FinalizeRoundRequest Finalize(string roomId, string operationId = "room-end-1",
        string tenantId = Tenant, string gameId = Game) => new()
    {
        RoomId = roomId, GameId = gameId, OperationId = operationId, TenantId = tenantId
    };

    private static ReadLeaderboardRequest Board(int maxEntries, string boardId = "points",
        string tenantId = Tenant, string gameId = Game) => new()
    {
        GameId = gameId, BoardId = boardId, MaxEntries = maxEntries, TenantId = tenantId
    };

    private static ListPendingSettlementsRequest Queue(int limit, string cursor = "",
        string tenantId = Tenant, string gameId = Game) => new()
    {
        GameId = gameId, Limit = limit, Cursor = cursor, TenantId = tenantId
    };

    [Fact]
    public async Task ReadyAnswersForTheControlPlane()
    {
        await using var rig = await SettlementRig.CreateAsync();
        var reply = await rig.Service.Ready(new Empty(), rig.Context("control-server", Instance));
        Assert.True(reply.Ready);
        Assert.Equal("1", reply.SchemaVersion);
    }

    [Fact]
    public async Task ReadyAnswersForTheGameHost()
    {
        await using var rig = await SettlementRig.CreateAsync();
        var reply = await rig.Service.Ready(new Empty(), rig.Context("game-server", Instance));
        Assert.True(reply.Ready);
        Assert.Equal("1", reply.SchemaVersion);
    }

    [Fact]
    public async Task ReadyRefusesTheStreamerFace()
    {
        await using var rig = await SettlementRig.CreateAsync();
        await rig.Rejects(() => rig.Service.Ready(new Empty(), rig.Context("streamer-server", Instance)),
            StatusCode.PermissionDenied, "Role is not authorized.");
    }

    [Fact]
    public async Task CommitResultRefusesAClusterCallerWithoutATenant()
    {
        await using var rig = await SettlementRig.CreateAsync();
        await rig.Rejects(() => rig.Service.CommitResult(Commit("room-1", tenantId: ""),
            rig.Context("control-server", Instance)),
            StatusCode.InvalidArgument, "A cluster caller must name the tenant it acts for.");
    }

    [Fact]
    public async Task CommitResultRefusesATenantCallerNamingAnotherTenant()
    {
        await using var rig = await SettlementRig.CreateAsync();
        var context = rig.TenantContext();
        await rig.Rejects(() => rig.Service.CommitResult(Commit("room-1", tenantId: "tenant-b"), context),
            StatusCode.PermissionDenied, "A tenant caller cannot act for another tenant.");
    }

    [Fact]
    public async Task CommitResultAppliesEveryGrantOfTheManifest()
    {
        await using var rig = await SettlementRig.CreateAsync();
        await rig.FinalFrameAsync("room-1", "settle-open-1", "settle-live-1");

        var reply = await rig.Service.CommitResult(Commit("room-1"), rig.Context("control-server", Instance));

        Assert.Equal("room-1", reply.RoomId);
        Assert.Equal(1, reply.ResultVersion);
        Assert.Equal(3, reply.GrantsTotal);
        Assert.Equal(3, reply.GrantsGranted);
        Assert.Equal(0, reply.GrantsPending);
        Assert.False(reply.Duplicate);
        Assert.Equal(3L, await rig.ScalarAsync(
            "SELECT count(*) FROM player.ledger WHERE operation_id LIKE 'room-1/grant/%'"));
        Assert.Equal(100L, await rig.ScalarAsync(
            "SELECT balance FROM player.asset WHERE player_id='p1' AND asset_id='points'"));
    }

    [Fact]
    public async Task CommitResultReplaysAsADuplicate()
    {
        await using var rig = await SettlementRig.CreateAsync();
        await rig.FinalFrameAsync("room-1", "settle-open-1", "settle-live-1");
        await rig.Service.CommitResult(Commit("room-1"), rig.Context("control-server", Instance));

        var replay = await rig.Service.CommitResult(Commit("room-1"), rig.Context("control-server", Instance));

        Assert.True(replay.Duplicate);
        Assert.Equal(3, replay.GrantsGranted);
        Assert.Equal(0, replay.GrantsPending);
        // The replay must not pay twice: the balances and the ledger stay at one pass.
        Assert.Equal(100L, await rig.ScalarAsync(
            "SELECT balance FROM player.asset WHERE player_id='p1' AND asset_id='points'"));
        Assert.Equal(3L, await rig.ScalarAsync(
            "SELECT count(*) FROM player.ledger WHERE operation_id LIKE 'room-1/grant/%'"));
    }

    [Fact]
    public async Task CommitResultRefusesARoomWithoutAResult()
    {
        await using var rig = await SettlementRig.CreateAsync();
        await rig.Rejects(() => rig.Service.CommitResult(Commit("room-x"), rig.Context("control-server", Instance)),
            StatusCode.FailedPrecondition, "The room has no committed result.");
    }

    [Fact]
    public async Task CommitResultRefusesAGrantThatWouldDriveTheAssetNegative()
    {
        await using var rig = await SettlementRig.CreateAsync();
        await rig.NegativeGrantFrameAsync("room-neg", "settle-open-neg", "settle-live-neg");

        await rig.Rejects(() => rig.Service.CommitResult(Commit("room-neg"), rig.Context("control-server", Instance)),
            StatusCode.FailedPrecondition, "The grant for p1 would drive the asset negative.");
        Assert.Equal(1L, await rig.ScalarAsync(
            "SELECT count(*) FROM settlement.grant WHERE room_id='room-neg' AND status='Planned'"));
    }

    [Fact]
    public async Task CommitResultInheritsTheCertificateTenant()
    {
        await using var rig = await SettlementRig.CreateAsync();
        await rig.FinalFrameAsync("room-1", "settle-open-1", "settle-live-1");
        var context = rig.TenantContext();

        var reply = await rig.Service.CommitResult(Commit("room-1", tenantId: ""), context);

        Assert.Equal(3, reply.GrantsGranted);
    }

    [Fact]
    public async Task SettleGrantsAppliesTheManifestUnderTheRoomSettleId()
    {
        await using var rig = await SettlementRig.CreateAsync();
        await rig.FinalFrameAsync("room-1", "settle-open-1", "settle-live-1");
        var request = new SettleGrantsRequest { GameId = Game, RoomId = "room-1", TenantId = Tenant };

        var reply = await rig.Service.SettleGrants(request, rig.Context("control-server", Instance));

        Assert.Equal(3, reply.GrantsGranted);
        Assert.Equal(0, reply.GrantsPending);
        Assert.False(reply.Duplicate);
    }

    [Fact]
    public async Task SettleGrantsSharesReceiptsAlongCommitResult()
    {
        await using var rig = await SettlementRig.CreateAsync();
        await rig.FinalFrameAsync("room-1", "settle-open-1", "settle-live-1");
        var request = new SettleGrantsRequest { GameId = Game, RoomId = "room-1", TenantId = Tenant };
        await rig.Service.SettleGrants(request, rig.Context("control-server", Instance));

        // The business id is the room's own settle id, so a replay through the other
        // face of the same call chain lands on the spent receipts, not a second pass.
        var replay = await rig.Service.CommitResult(Commit("room-1", operationId: "room-1/settle"),
            rig.Context("control-server", Instance));

        Assert.True(replay.Duplicate);
        Assert.Equal(0, replay.GrantsPending);
    }

    [Fact]
    public async Task SettleGrantsRefusesTheGameServerRole()
    {
        await using var rig = await SettlementRig.CreateAsync();
        var request = new SettleGrantsRequest { GameId = Game, RoomId = "room-1", TenantId = Tenant };
        await rig.Rejects(() => rig.Service.SettleGrants(request, rig.Context("game-server", Instance)),
            StatusCode.PermissionDenied, "Role is not authorized.");
    }

    [Fact]
    public async Task FinalizeRoundReleasesARoomThatMeetsAllTheEvidence()
    {
        await using var rig = await SettlementRig.CreateAsync();
        var room = await rig.FinalFrameAsync("room-1", "settle-open-1", "settle-live-1");
        await EndLadderEvidence.StandAsync(rig.Database.ConnectionString, "room-1");
        await rig.Service.CommitResult(Commit("room-1"), rig.Context("control-server", Instance));

        var reply = await rig.Service.FinalizeRound(Finalize("room-1", operationId: "room-1/end"),
            rig.Context("control-server", Instance));

        Assert.Equal("room-1", reply.RoomId);
        Assert.Equal("Settled", reply.Status);
        Assert.Equal(room.Epoch + 1, reply.Epoch);
        Assert.False(reply.Duplicate);
        Assert.Equal(1L, await rig.ScalarAsync($$"""
            SELECT count(*) FROM live.binding WHERE binding_id='{{room.BindingId}}' AND current_room_id IS NULL
            """));
        Assert.Equal(1L, await rig.ScalarAsync(
            "SELECT count(*) FROM game.participation WHERE room_id='room-1' AND state='Closed'"));
        Assert.Equal("Released:" + (room.Epoch + 1), await rig.ScalarAsync(
            "SELECT status||':'||epoch FROM control.ownership WHERE kind='room' AND entity_id='room-1'"));
    }

    [Fact]
    public async Task FinalizeRoundReplaysAsADuplicate()
    {
        await using var rig = await SettlementRig.CreateAsync();
        await rig.FinalFrameAsync("room-1", "settle-open-1", "settle-live-1");
        await EndLadderEvidence.StandAsync(rig.Database.ConnectionString, "room-1");
        await rig.Service.CommitResult(Commit("room-1"), rig.Context("control-server", Instance));
        var first = await rig.Service.FinalizeRound(Finalize("room-1", operationId: "room-1/end"),
            rig.Context("control-server", Instance));

        var replay = await rig.Service.FinalizeRound(Finalize("room-1", operationId: "room-1/end"),
            rig.Context("control-server", Instance));

        Assert.True(replay.Duplicate);
        Assert.Equal(first.Status, replay.Status);
        Assert.Equal(first.Epoch, replay.Epoch);
    }

    [Fact]
    public async Task FinalizeRoundRefusesAnAlreadySettledRoom()
    {
        await using var rig = await SettlementRig.CreateAsync();
        await rig.FinalFrameAsync("room-1", "settle-open-1", "settle-live-1");
        await EndLadderEvidence.StandAsync(rig.Database.ConnectionString, "room-1");
        await rig.Service.CommitResult(Commit("room-1"), rig.Context("control-server", Instance));
        await rig.Service.FinalizeRound(Finalize("room-1", operationId: "room-1/end"),
            rig.Context("control-server", Instance));

        // A fresh operation id runs the store's checks again and meets the settled room.
        await rig.Rejects(() => rig.Service.FinalizeRound(Finalize("room-1", operationId: "room-1/end-2"),
                rig.Context("control-server", Instance)),
            StatusCode.FailedPrecondition, "The room is already settled.");
    }

    [Fact]
    public async Task FinalizeRoundRefusesAnUnknownRoom()
    {
        await using var rig = await SettlementRig.CreateAsync();
        await rig.Rejects(() => rig.Service.FinalizeRound(Finalize("room-zz"), rig.Context("control-server", Instance)),
            StatusCode.FailedPrecondition, "The room does not exist.");
    }

    [Fact]
    public async Task FinalizeRoundRefusesARoomStillRunning()
    {
        await using var rig = await SettlementRig.CreateAsync();
        await rig.AdmitAsync("room-1", "settle-open-1", "settle-live-1");

        await rig.Rejects(() => rig.Service.FinalizeRound(Finalize("room-1"), rig.Context("control-server", Instance)),
            StatusCode.FailedPrecondition, "A room in status Running cannot be finalized.");
    }

    [Fact]
    public async Task FinalizeRoundRefusesARoomWithoutEndOperationEvidence()
    {
        await using var rig = await SettlementRig.CreateAsync();
        await rig.FinalFrameAsync("room-1", "settle-open-1", "settle-live-1");

        await rig.Rejects(() => rig.Service.FinalizeRound(Finalize("room-1"), rig.Context("control-server", Instance)),
            StatusCode.FailedPrecondition,
            "The room has no end operation; the release waits for the end ladder's evidence.");
    }

    [Fact]
    public async Task FinalizeRoundRefusesAnEndOperationBeforeThePlatformClose()
    {
        await using var rig = await SettlementRig.CreateAsync();
        await rig.FinalFrameAsync("room-1", "settle-open-1", "settle-live-1");
        await EndLadderEvidence.StandAsync(rig.Database.ConnectionString, "room-1", phase: "PlatformClosing");

        await rig.Rejects(() => rig.Service.FinalizeRound(Finalize("room-1"), rig.Context("control-server", Instance)),
            StatusCode.FailedPrecondition,
            "The end operation stands at PlatformClosing; the release waits for the platform close evidence.");
    }

    [Fact]
    public async Task FinalizeRoundRefusesARoomWithoutACommittedResult()
    {
        await using var rig = await SettlementRig.CreateAsync();
        await rig.AdmitAsync("room-1", "settle-open-1", "settle-live-1");
        await rig.ExecuteAsync("UPDATE game.room SET status='Finishing' WHERE room_id='room-1'");
        await EndLadderEvidence.StandAsync(rig.Database.ConnectionString, "room-1");

        await rig.Rejects(() => rig.Service.FinalizeRound(Finalize("room-1"), rig.Context("control-server", Instance)),
            StatusCode.FailedPrecondition, "The room has no committed result to finalize.");
    }

    [Fact]
    public async Task FinalizeRoundRefusesAnUnsettledManifest()
    {
        await using var rig = await SettlementRig.CreateAsync();
        await rig.FinalFrameAsync("room-1", "settle-open-1", "settle-live-1");
        await EndLadderEvidence.StandAsync(rig.Database.ConnectionString, "room-1");

        await rig.Rejects(() => rig.Service.FinalizeRound(Finalize("room-1"), rig.Context("control-server", Instance)),
            StatusCode.FailedPrecondition,
            "The reward manifest is incomplete: settle the grants before finalizing.");
    }

    [Fact]
    public async Task FinalizeRoundRefusesARoomWithUndisposedInputs()
    {
        await using var rig = await SettlementRig.CreateAsync();
        await rig.FinalFrameAsync("room-1", "settle-open-1", "settle-live-1", pendingInput: true);
        await EndLadderEvidence.StandAsync(rig.Database.ConnectionString, "room-1");
        await rig.Service.CommitResult(Commit("room-1"), rig.Context("control-server", Instance));

        await rig.Rejects(() => rig.Service.FinalizeRound(Finalize("room-1"), rig.Context("control-server", Instance)),
            StatusCode.FailedPrecondition,
            "The round still has undisposed inputs; finalize after they reach a terminal state.");
    }

    [Fact]
    public async Task ReadLeaderboardOrdersScoresDeterministically()
    {
        await using var rig = await SettlementRig.CreateAsync();
        await rig.FinalFrameAsync("room-1", "settle-open-1", "settle-live-1");
        await rig.Service.CommitResult(Commit("room-1"), rig.Context("control-server", Instance));

        var reply = await rig.Service.ReadLeaderboard(Board(10), rig.Context("control-server", Instance));

        Assert.Equal("points", reply.BoardId);
        Assert.Equal(3, reply.Entries.Count);
        Assert.Equal("p1", reply.Entries[0].PlayerId);
        Assert.Equal(100, reply.Entries[0].Score);
        Assert.Equal("p2", reply.Entries[1].PlayerId);
        Assert.Equal(30, reply.Entries[1].Score);
        Assert.Equal("p3", reply.Entries[2].PlayerId);
        Assert.Equal(30, reply.Entries[2].Score);
    }

    [Fact]
    public async Task ReadLeaderboardBoundsThePage()
    {
        await using var rig = await SettlementRig.CreateAsync();
        await rig.FinalFrameAsync("room-1", "settle-open-1", "settle-live-1");
        await rig.Service.CommitResult(Commit("room-1"), rig.Context("control-server", Instance));

        var reply = await rig.Service.ReadLeaderboard(Board(2), rig.Context("control-server", Instance));

        Assert.Equal(2, reply.Entries.Count);
        Assert.Equal("p1", reply.Entries[0].PlayerId);
        Assert.Equal("p2", reply.Entries[1].PlayerId);
    }

    [Fact]
    public async Task ReadLeaderboardAnswersForTheStreamerFace()
    {
        await using var rig = await SettlementRig.CreateAsync();
        await rig.FinalFrameAsync("room-1", "settle-open-1", "settle-live-1");
        await rig.Service.CommitResult(Commit("room-1"), rig.Context("control-server", Instance));

        var reply = await rig.Service.ReadLeaderboard(Board(10), rig.Context("streamer-server", Instance));

        Assert.Equal(3, reply.Entries.Count);
    }

    [Fact]
    public async Task ReadLeaderboardRefusesAnEmptyWindow()
    {
        await using var rig = await SettlementRig.CreateAsync();
        await rig.Rejects(() => rig.Service.ReadLeaderboard(Board(0), rig.Context("control-server", Instance)),
            StatusCode.InvalidArgument, detailContains: "(Parameter 'maxEntries')");
    }

    [Fact]
    public async Task ListPendingSettlementsListsOnlyUnsettledRooms()
    {
        await using var rig = await SettlementRig.CreateAsync();
        await rig.FinalFrameAsync("room-a", "settle-open-a", "settle-live-a");
        await rig.FinalFrameAsync("room-b", "settle-open-b", "settle-live-b");
        await rig.Service.CommitResult(Commit("room-b"), rig.Context("control-server", Instance));

        var reply = await rig.Service.ListPendingSettlements(Queue(10),
            rig.Context("control-server", Instance));

        var pending = Assert.Single(reply.Settlements);
        Assert.Equal("room-a", pending.RoomId);
        Assert.Equal(1, pending.ResultVersion);
        Assert.Equal(3, pending.GrantsTotal);
        Assert.Equal(0, pending.GrantsGranted);
        Assert.Equal(3, pending.GrantsPending);
        Assert.Equal("", reply.NextCursor);
    }

    [Fact]
    public async Task ListPendingSettlementsPagesByCursor()
    {
        await using var rig = await SettlementRig.CreateAsync();
        await rig.FinalFrameAsync("room-a", "settle-open-a", "settle-live-a");
        await rig.FinalFrameAsync("room-b", "settle-open-b", "settle-live-b");
        var context = rig.Context("control-server", Instance);

        var first = await rig.Service.ListPendingSettlements(Queue(1), context);
        var second = await rig.Service.ListPendingSettlements(Queue(1, first.NextCursor), context);
        var third = await rig.Service.ListPendingSettlements(Queue(1, second.NextCursor), context);

        var one = Assert.Single(first.Settlements);
        Assert.Equal("room-a", one.RoomId);
        Assert.Equal("room-a", first.NextCursor);
        var two = Assert.Single(second.Settlements);
        Assert.Equal("room-b", two.RoomId);
        Assert.Equal("room-b", second.NextCursor);
        Assert.Empty(third.Settlements);
        Assert.Equal("", third.NextCursor);
    }

    [Fact]
    public async Task ListPendingSettlementsRefusesAnOutOfRangePage()
    {
        await using var rig = await SettlementRig.CreateAsync();
        await rig.Rejects(() => rig.Service.ListPendingSettlements(Queue(501), rig.Context("control-server", Instance)),
            StatusCode.InvalidArgument, detailContains: "The discovery page limit must be within 1..500: 501");
    }

    [Fact]
    public async Task ListPendingSettlementsRefusesTheGameServerRole()
    {
        await using var rig = await SettlementRig.CreateAsync();
        await rig.Rejects(() => rig.Service.ListPendingSettlements(Queue(10), rig.Context("game-server", Instance)),
            StatusCode.PermissionDenied, "Role is not authorized.");
    }

    [Fact]
    public async Task CommitResultRefusesBlankCoordinates()
    {
        await using var rig = await SettlementRig.CreateAsync();
        await rig.Rejects(() => rig.Service.CommitResult(Commit("room-1", gameId: ""),
            rig.Context("control-server", Instance)),
            StatusCode.InvalidArgument, detailContains: "Value is required.");
    }

    [Fact]
    public async Task SettleGrantsRefusesARoomWithoutAResult()
    {
        await using var rig = await SettlementRig.CreateAsync();
        var request = new SettleGrantsRequest { GameId = Game, RoomId = "room-zz", TenantId = Tenant };
        await rig.Rejects(() => rig.Service.SettleGrants(request, rig.Context("control-server", Instance)),
            StatusCode.FailedPrecondition, "The room has no committed result.");
    }

    [Fact]
    public async Task SettleGrantsRefusesBlankCoordinates()
    {
        await using var rig = await SettlementRig.CreateAsync();
        var request = new SettleGrantsRequest { GameId = Game, RoomId = "", TenantId = Tenant };
        await rig.Rejects(() => rig.Service.SettleGrants(request, rig.Context("control-server", Instance)),
            StatusCode.InvalidArgument, detailContains: "Value is required.");
    }

    [Fact]
    public async Task FinalizeRoundRefusesBlankCoordinates()
    {
        await using var rig = await SettlementRig.CreateAsync();
        await rig.Rejects(() => rig.Service.FinalizeRound(Finalize("room-1", operationId: ""),
            rig.Context("control-server", Instance)),
            StatusCode.InvalidArgument, detailContains: "Value is required.");
    }

    [Fact]
    public async Task FinalizeRoundTreatsAnOperationIdSpentUnderAnotherRoomAsFresh()
    {
        await using var rig = await SettlementRig.CreateAsync();
        await rig.FinalFrameAsync("room-1", "settle-open-1", "settle-live-1");
        await EndLadderEvidence.StandAsync(rig.Database.ConnectionString, "room-1");
        await rig.Service.CommitResult(Commit("room-1"), rig.Context("control-server", Instance));
        await rig.Service.FinalizeRound(Finalize("room-1", operationId: "room-1/end"),
            rig.Context("control-server", Instance));

        // The finalize receipt is scoped by its room: an operation id spent under
        // room-1 is a fresh receipt for room-zz, not a replay — the grant lines still
        // apply under their own room-scoped ids and the account ends settled twice.
        await rig.FinalFrameAsync("room-zz", "settle-open-zz", "settle-live-zz");
        await EndLadderEvidence.StandAsync(rig.Database.ConnectionString, "room-zz");
        await rig.Service.CommitResult(Commit("room-zz", operationId: "settle-op-zz"),
            rig.Context("control-server", Instance));
        await rig.Service.FinalizeRound(Finalize("room-zz", operationId: "room-1/end"),
            rig.Context("control-server", Instance));
        Assert.Equal(200L, await rig.ScalarAsync(
            "SELECT balance FROM player.asset WHERE player_id='p1' AND asset_id='points'"));
    }

    [Fact]
    public async Task CommitResultRefusesASpentGrantOperationIdUnderADifferentShape()
    {
        await using var rig = await SettlementRig.CreateAsync();
        await rig.FinalFrameAsync("room-1", "settle-open-1", "settle-live-1");
        await rig.Service.CommitResult(Commit("room-1"), rig.Context("control-server", Instance));

        // Grant identities are room-scoped, so a reused grant id reaches the ledger —
        // whose key spans the game — and the store refuses it there, before the grant
        // transaction can move any account twice under one operation id.
        await rig.GrantFrameAsync("room-2", "settle-open-2", "settle-live-2",
            [new PlannedGrant("p1", "round-score", "room-1/grant/p1", "points", 50)]);
        await rig.Rejects(() => rig.Service.CommitResult(Commit("room-2", operationId: "settle-op-2"),
                rig.Context("control-server", Instance)),
            StatusCode.FailedPrecondition,
            "The grant operation id room-1/grant/p1 was already spent under another room.");
        Assert.Equal(100L, await rig.ScalarAsync(
            "SELECT balance FROM player.asset WHERE player_id='p1' AND asset_id='points'"));
    }

    private sealed record Room(string RoomId, string BindingId, string StreamerId, long Epoch);

    private sealed record SettlementRig(SettlementGrpcService Service, RedesignFrameStore Frames,
        RedesignRoomStore Rooms, RedesignControlStore Control, RedesignIdentityStore Identity,
        RedesignBindingStore Bindings, RedesignInputEventStore Events, RedesignRoundInputStore Inputs,
        TestProcessCertificates Certificates, TemporaryDatabase Database) : IAsyncDisposable
    {
        public static async Task<SettlementRig> CreateAsync()
        {
            var database = await TemporaryDatabase.CreateAsync();
            var certificates = new TestProcessCertificates();
            var connection = database.ConnectionString;
            var service = new SettlementGrpcService(new RedesignSettlementStore(connection),
                certificates.Trusted);
            return new SettlementRig(service, new RedesignFrameStore(connection),
                new RedesignRoomStore(connection), new RedesignControlStore(connection),
                new RedesignIdentityStore(connection), new RedesignBindingStore(connection),
                new RedesignInputEventStore(connection), new RedesignRoundInputStore(connection),
                certificates, database);
        }

        /// <summary>Mounts a binding, admits the room and activates it (tick 0, version 1) —
        /// the exact chain the face's rooms walk before any settlement fact.</summary>
        public async Task<Room> AdmitAsync(string roomId, string openId, string liveKey)
        {
            var streamer = await Identity.ResolveIdentityAsync(Tenant, "simulator", "app-1",
                openId + "-open");
            var live = await Bindings.OpenLiveSessionAsync(Tenant, streamer.StreamerId, "simulator",
                "app-1", "platform-room", liveKey);
            var session = await Identity.LoginAsync(streamer.StreamerId, "gateway-1",
                "connection-" + openId, RandomNumberGenerator.GetBytes(32));
            var binding = await Bindings.StartGameplayAsync(Tenant, live, session.SessionId, Game,
                "mount-" + openId);
            var claim = await Control.ClaimOwnershipAsync(Tenant, Game, "room", roomId, "gs-1", 60);
            await Rooms.StartRoundAsync(Tenant, Game, roomId, "gs-1", claim.Epoch,
                [new RoundParticipant(binding.BindingId, streamer.StreamerId, "Host")],
                "1.0.0", "cfg-1", "pkg-1", "start-" + roomId);
            await Rooms.PrepareRoundAsync(Tenant, Game, roomId, "gs-1", claim.Epoch,
                [1, 2, 3], [4, 5, 6], 1, "prepare-" + roomId);
            await Rooms.ActivateRoundAsync(Tenant, Game, roomId, "gs-1", claim.Epoch,
                "activate-" + roomId);
            return new Room(roomId, binding.BindingId, streamer.StreamerId, claim.Epoch);
        }

        /// <summary>Commits the final frame carrying the three-grant manifest; the room
        /// lands Finishing with p1/p2/p3 planned for 100/30/30 points.</summary>
        public async Task<Room> FinalFrameAsync(string roomId, string openId, string liveKey,
            bool pendingInput = false)
        {
            var room = await AdmitAsync(roomId, openId, liveKey);
            if (pendingInput) await RoutePendingGiftAsync(liveKey);
            await Frames.CommitFrameAsync(Tenant, Game, roomId, FinalCommit(roomId, room.Epoch,
            [
                new PlannedGrant("p1", "round-score", roomId + "/grant/p1", "points", 100),
                new PlannedGrant("p2", "round-score", roomId + "/grant/p2", "points", 30),
                new PlannedGrant("p3", "round-score", roomId + "/grant/p3", "points", 30)
            ]));
            return room;
        }

        /// <summary>A final frame carrying caller-chosen grants — the overdraw and
        /// receipt-collision facts pin their manifests directly.</summary>
        public async Task<Room> GrantFrameAsync(string roomId, string openId, string liveKey,
            IReadOnlyList<PlannedGrant> grants)
        {
            var room = await AdmitAsync(roomId, openId, liveKey);
            await Frames.CommitFrameAsync(Tenant, Game, roomId, FinalCommit(roomId, room.Epoch, grants));
            return room;
        }

        /// <summary>A final frame whose only planned grant would overdraw a fresh account.</summary>
        public Task<Room> NegativeGrantFrameAsync(string roomId, string openId, string liveKey) =>
            GrantFrameAsync(roomId, openId, liveKey,
                [new PlannedGrant("p1", "fee", roomId + "/grant/p1", "points", -500)]);

        private static FrameCommit FinalCommit(string roomId, long epoch,
            IReadOnlyList<PlannedGrant> grants) => new(
            "final-" + roomId, "gs-1", epoch, 0, 1, [], [9, 9, 9], [], "1.0.0", "cfg-1",
            Final: true, ResultJson: """{"winner":"red"}""",
            PlanHash: SHA256.HashData(Encoding.UTF8.GetBytes("""{"winner":"red"}""")),
            Grants: grants);

        /// <summary>Routes one gift into the room's inbox and leaves it Pending.</summary>
        public async Task RoutePendingGiftAsync(string liveKey)
        {
            await Events.IngestAsync("simulator", "app-1", "route-batch-" + liveKey,
            [
                new PlatformEventInput("gift-" + liveKey, RedesignInputEventStore.Durable,
                    liveKey, """{"kind":"gift","amount":5}""")
            ]);
            await Inputs.RouteEventsAsync("simulator", "app-1", "route-op-" + liveKey, 50);
        }

        public async Task ExecuteAsync(string sql)
        {
            await using var connection = new NpgsqlConnection(Database.ConnectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(sql, connection);
            await command.ExecuteNonQueryAsync();
        }

        public async Task<object?> ScalarAsync(string sql)
        {
            await using var connection = new NpgsqlConnection(Database.ConnectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(sql, connection);
            return await command.ExecuteScalarAsync();
        }

        public TestServerCallContext Context(string role, string instance) =>
            Certificates.Context(Certificates.Issue("@cluster", role, instance));

        /// <summary>A tenant-scope caller inherits its certificate tenant and names no other.</summary>
        public TestServerCallContext TenantContext() =>
            Certificates.Context(Certificates.Issue(Tenant, "game-server", Instance));

        public async Task Rejects(Func<Task> call, StatusCode code, string? detail = null,
            string? detailContains = null)
        {
            var error = await Assert.ThrowsAsync<RpcException>(call);
            Assert.Equal(code, error.Status.StatusCode);
            if (detail is not null) Assert.Equal(detail, error.Status.Detail);
            if (detailContains is not null) Assert.Contains(detailContains, error.Status.Detail);
        }

        public async ValueTask DisposeAsync() => await Database.DisposeAsync();
    }

    private sealed class TemporaryDatabase(string source) : IAsyncDisposable
    {
        private readonly string _name = "chorus_settle_face_" + Guid.NewGuid().ToString("N");
        private readonly NpgsqlConnectionStringBuilder _admin = new(source) { Pooling = false };
        public string ConnectionString { get; private set; } = "";

        public static async Task<TemporaryDatabase> CreateAsync()
        {
            var source = Environment.GetEnvironmentVariable("CHORUS_TEST_POSTGRES")
                ?? throw new InvalidOperationException("Set CHORUS_TEST_POSTGRES to a PostgreSQL role with CREATEDB.");
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
            // DROP ... WITH (FORCE) collects the database's backends under one lock and
            // checks each pid's privileges after releasing it: a backend that exits in
            // between can have its OS pid reused by another role's connection under a
            // co-tenant fork storm, and the privilege check then lands on that stranger
            // and fails with 42501. The exiting backend is really gone within
            // milliseconds, so a short bounded retry settles it.
            await using var admin = new NpgsqlConnection(_admin.ConnectionString);
            await admin.OpenAsync();
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    await using var drop = new NpgsqlCommand(
                        $"DROP DATABASE IF EXISTS \"{_name}\" WITH (FORCE)", admin);
                    await drop.ExecuteNonQueryAsync();
                    return;
                }
                catch (PostgresException error) when (error.SqlState == "42501" && attempt < 5)
                {
                    await Task.Delay(100 * attempt);
                }
            }
        }
    }
}

using Chorus.Data;
using Chorus.Rpc;
using Grpc.Core;
using System.Text.Json;

namespace Chorus.ControlServer;

/// <summary>
/// The admin boundary (operations 6): diagnostics that join the whole footprint of a streamer
/// or a room, the AT31 round trace (event → delivery → frame → effects → result → grants →
/// ledger), and controlled failure commands. Every command names a request id, an operator and
/// a reason; the audit row is written first, so a command replays as a visible duplicate
/// instead of acting twice — a failed command keeps its request id spent, and the operator
/// retries under a new one. Reads never mutate.
/// </summary>
public sealed class AdminGrpcService(
    ClusterDirectory directory, RedesignAdminStore adminStore, RedesignControlStore controlStore,
    RedesignOperatorStore operatorStore, TrustedProcessCertificates trustedCertificates,
    LeaderboardQueryService leaderboardQueries) : Admin.AdminBase
{
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(10);

    private readonly Settlement.SettlementClient _settlement = new(directory.DataChannel);
    private readonly PlatformTask.PlatformTaskClient _tasks = new(directory.DataChannel);
    // The one room-recovery flow (architecture 10.3, FIX-REC-01): the operator's retry
    // drives the same barriered orchestration TakeoverRoom does — never a second
    // implementation.
    private readonly TakeoverGrpcService _takeover =
        new(directory, controlStore, trustedCertificates);

    public override Task<ReadyReply> Ready(Empty request, ServerCallContext context)
    {
        Require(context, "control-server");
        return Task.FromResult(new ReadyReply { Ready = true, SchemaVersion = "4" });
    }

    /// <summary>The browser health word and login probe (FIX-WEB-01 / FIX-WEB-03): the caller's
    /// own operator identity, resolved from the bearer credential — never the request body. A
    /// credential that authenticates is by definition enabled (a disabled operator is refused
    /// at the resolve), so the reply's enabled flag is always true here; the flag exists for
    /// the contract shape, not as an extra state an authenticated caller could report false.</summary>
    public override async Task<AdminWhoAmIReply> WhoAmI(Empty request, ServerCallContext context)
    {
        var token = ReadOperatorToken(context);
        if (token is null)
            throw new RpcException(new Status(StatusCode.Unauthenticated, "Operator credentials are absent."));
        var operatorIdentity = await operatorStore.ResolveOperatorAsync(token, context.CancellationToken);
        if (operatorIdentity is null)
            throw new RpcException(new Status(StatusCode.Unauthenticated, "Operator credential is invalid."));
        return new AdminWhoAmIReply
        {
            OperatorId = operatorIdentity.OperatorId, Role = operatorIdentity.Role,
            Tenants = { operatorIdentity.Tenants }, Games = { operatorIdentity.Games },
            Enabled = true
        };
    }

    /// <summary>The read half of the GM room-epoch entry (FIX-GM-01 / FIX-GM-06). The operator
    /// identity comes from the bearer credential, never the request body: an operator may read
    /// only what their role and tenant/game scope cover, and the request's own operator field
    /// must agree with the authenticated operator or it is a forged identity.</summary>
    public override async Task<AdminGetRoomEpochReply> GetRoomEpoch(
        AdminGetRoomEpochRequest request, ServerCallContext context)
    {
        if (string.IsNullOrWhiteSpace(request.TenantId) || string.IsNullOrWhiteSpace(request.GameId) ||
            string.IsNullOrWhiteSpace(request.RoomId))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "The epoch request is incomplete."));
        var operatorIdentity = await RequireOperator(context, "gm", request.TenantId, request.GameId);
        if (request.Operator != operatorIdentity.OperatorId)
            throw new RpcException(new Status(StatusCode.PermissionDenied,
                "The request operator does not match the authenticated operator."));
        var epoch = await controlStore.GetRoomEpochAsync(
            request.TenantId, request.GameId, request.RoomId, context.CancellationToken);
        return new AdminGetRoomEpochReply
        {
            Found = epoch.Found, Epoch = epoch.Epoch,
            OwnerInstanceId = epoch.OwnerInstanceId ?? "", Status = epoch.Status ?? "",
            TenantId = request.TenantId, GameId = request.GameId, RoomId = request.RoomId
        };
    }

    /// <summary>The audited, idempotent epoch bump (FIX-GM-01 / FIX-GM-02 / FIX-GM-03). The
    /// operator gate and forged-identity check mirror GetRoomEpoch; the storage transaction is
    /// the FIX-GM-02 line. The route change is a separate surface from the storage commit: the
    /// storage moves the epoch (old/new in the reply), the route publication moves the routing
    /// version (the epoch is part of the snapshot content hash) and the gateway's confirmation
    /// is what the caller sees as route_sync_status — never "Synced" before the gateway
    /// confirms, and never an implicit platform-end or room-end side effect.</summary>
    public override async Task<AdminBumpRoomEpochReply> BumpRoomEpoch(
        AdminBumpRoomEpochRequest request, ServerCallContext context)
    {
        if (string.IsNullOrWhiteSpace(request.TenantId) || string.IsNullOrWhiteSpace(request.GameId) ||
            string.IsNullOrWhiteSpace(request.RoomId) || string.IsNullOrWhiteSpace(request.RequestId) ||
            string.IsNullOrWhiteSpace(request.Reason))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "The bump request is incomplete."));
        var operatorIdentity = await RequireOperator(context, "gm", request.TenantId, request.GameId);
        if (request.Operator != operatorIdentity.OperatorId)
            throw new RpcException(new Status(StatusCode.PermissionDenied,
                "The request operator does not match the authenticated operator."));

        EpochBumpOutcome outcome;
        try
        {
            outcome = await controlStore.BumpRoomEpochAsync(request.TenantId, request.GameId, request.RoomId,
                request.ExpectedEpoch, request.TargetEpoch, request.RequestId, request.Operator, request.Reason,
                leaseSeconds: 60, context.CancellationToken);
        }
        catch (IdempotencyConflictException)
        {
            throw new RpcException(new Status(StatusCode.AlreadyExists,
                "The request id is already spent by a different request."));
        }
        catch (ArgumentOutOfRangeException error)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, error.Message));
        }
        catch (InvalidOperationException error)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, error.Message));
        }
        // The storage commit and the route sync are distinct surfaces; the gateway confirmation
        // is the only thing that ever reports Synced.
        var routeStatus = await controlStore.GetRoomRouteSyncStatusAsync(
            request.TenantId, request.GameId, request.RoomId, context.CancellationToken);
        return new AdminBumpRoomEpochReply
        {
            OperationId = request.RequestId, RoomId = request.RoomId,
            OwnerInstanceId = outcome.OwnerInstanceId, OldEpoch = outcome.OldEpoch, NewEpoch = outcome.NewEpoch,
            OwnershipStatus = outcome.OwnershipStatus, RouteSyncStatus = routeStatus,
            Duplicate = outcome.Duplicate, TenantId = request.TenantId, GameId = request.GameId
        };
    }

    /// <summary>The targeted playtest dispatch (FIX-GM-07, architecture 12.1's room command
    /// row). The operator gate and forged-identity check mirror the epoch entries; the route
    /// is resolved here — the operator never names an endpoint — and the owner host is the
    /// target-version authority that re-validates owner and epoch before the command rides
    /// the durable ordered-input path. The audit row is written first, so a replayed request
    /// id answers as a visible duplicate of the durable input (its stage read from the data
    /// plane) instead of a second effect. The reply's stage is never "Applied": application
    /// is what GetOperationStatus proves from the committed frame record.</summary>
    public override async Task<AdminDispatchRoomCommandReply> DispatchRoomCommand(
        AdminDispatchRoomCommandRequest request, ServerCallContext context)
    {
        if (Incomplete(request.RequestId, request.Operator, request.Reason) ||
            string.IsNullOrWhiteSpace(request.TenantId) || string.IsNullOrWhiteSpace(request.GameId) ||
            string.IsNullOrWhiteSpace(request.RoomId) || string.IsNullOrWhiteSpace(request.ParticipantId) ||
            string.IsNullOrWhiteSpace(request.PayloadJson))
            throw new RpcException(new Status(StatusCode.InvalidArgument,
                "The dispatch needs a request id, an operator, a reason, a target room, a participant and a payload."));
        var operatorIdentity = await RequireOperator(context, "gm", request.TenantId, request.GameId);
        if (request.Operator != operatorIdentity.OperatorId)
            throw new RpcException(new Status(StatusCode.PermissionDenied,
                "The request operator does not match the authenticated operator."));
        try { using (JsonDocument.Parse(request.PayloadJson)) { } }
        catch (JsonException)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "The payload is not valid JSON."));
        }
        var epoch = await controlStore.GetRoomEpochAsync(
            request.TenantId, request.GameId, request.RoomId, context.CancellationToken);
        if (!epoch.Found)
            throw new RpcException(new Status(StatusCode.NotFound, "The room has no ownership row."));
        if (request.ExpectedEpoch > 0 && request.ExpectedEpoch != epoch.Epoch)
            throw new RpcException(new Status(StatusCode.FailedPrecondition,
                $"The room is at epoch {epoch.Epoch}, not the expected {request.ExpectedEpoch}; re-read the snapshot."));
        var acted = await adminStore.WriteAuditAsync(request.RequestId, request.Operator, request.Reason,
            request.RoomId, "DispatchRoomCommand",
            $$"""{"room":"{{request.RoomId}}","participant":"{{request.ParticipantId}}","owner":"{{epoch.OwnerInstanceId ?? ""}}","epoch":{{epoch.Epoch}}}""",
            context.CancellationToken);
        if (!acted)
        {
            // A replayed request id: the durable input's stage is the answer, and a lost
            // original (audit stands, dispatch never landed) says so — the operator issues a
            // fresh id, never a re-send that could double an applied command.
            var lookup = await new RoundInput.RoundInputClient(directory.DataChannel).LookupInputAsync(
                new LookupInputRequest { EventId = request.RequestId },
                deadline: DateTime.UtcNow.Add(CallTimeout), cancellationToken: context.CancellationToken);
            return new AdminDispatchRoomCommandReply
            {
                OperationId = request.RequestId, RoomId = request.RoomId,
                OwnerInstanceId = epoch.OwnerInstanceId ?? "", Epoch = epoch.Epoch,
                Stage = lookup.Found ? OperationStage(lookup) : "NotFound",
                Duplicate = true,
                Note = lookup.Found ? "This operation id already stands." :
                    "This request id is audited but produced no durable input; issue a new request id.",
                TenantId = request.TenantId, GameId = request.GameId
            };
        }
        var channel = directory.GameHostChannel(epoch.OwnerInstanceId ?? "")
            ?? throw new RpcException(new Status(StatusCode.NotFound,
                $"The room's owner '{epoch.OwnerInstanceId}' is not a known game host."));
        // The host's own fence answers a room it no longer runs; the call deadline covers the
        // host's binding read plus its durable accept (each internally bounded at 10s).
        var delivered = await new GameHost.GameHostClient(channel).DeliverRoomCommandAsync(
            new DeliverRoomCommandRequest
            {
                TenantId = request.TenantId, GameId = request.GameId, RoomId = request.RoomId,
                OperationId = request.RequestId, ParticipantId = request.ParticipantId,
                PayloadJson = request.PayloadJson
            }, deadline: DateTime.UtcNow.AddSeconds(30), cancellationToken: context.CancellationToken);
        return new AdminDispatchRoomCommandReply
        {
            OperationId = delivered.OperationId, RoomId = request.RoomId,
            OwnerInstanceId = delivered.OwnerInstanceId, Epoch = delivered.Epoch,
            Stage = delivered.Disposition, Duplicate = delivered.Disposition == DuplicateText,
            Note = delivered.Note, TenantId = request.TenantId, GameId = request.GameId
        };
    }

    /// <summary>The effect query by the original operation id (FIX-GM-07, architecture 12.1:
    /// accepted, applied and unknown are distinct answers; a timeout is queried, never
    /// re-sent under a new id). The data plane holds the whole answer for room commands —
    /// the input row's acceptance, the delivery state and the committed tick whose frame
    /// consumed the command — so the control plane only translates it into stages. The
    /// control-plane commands (queue gate, drain, settlement retry, task reconcile, epoch
    /// bump) have no data-plane input: their operation id stands in the audit trail, so a
    /// miss falls back to the audit row (FIX-WEB-09) and answers "Audited" with the
    /// recorded evidence — the command's outcome was the synchronous reply, the row is what
    /// proves it happened. The scope fields are optional: a room command is scoped by the
    /// request, a control-plane operation is known by its id alone.</summary>
    public override async Task<AdminGetOperationStatusReply> GetOperationStatus(
        AdminGetOperationStatusRequest request, ServerCallContext context)
    {
        if (string.IsNullOrWhiteSpace(request.OperationId))
            throw new RpcException(new Status(StatusCode.InvalidArgument,
                "The status query needs an operation id."));
        // The status query is a read (FIX-WEB-01): relaxed from the gm floor to viewer.
        // Empty scope dimensions mean no constraint (FIX-WEB-03's InScope semantics).
        await RequireOperator(context, "viewer", request.TenantId, request.GameId);
        var lookup = await new RoundInput.RoundInputClient(directory.DataChannel).LookupInputAsync(
            new LookupInputRequest { EventId = request.OperationId },
            deadline: DateTime.UtcNow.Add(CallTimeout), cancellationToken: context.CancellationToken);
        if (!lookup.Found)
        {
            var audit = await adminStore.LookupOperationAsync(request.OperationId, context.CancellationToken);
            if (audit is null)
                return new AdminGetOperationStatusReply
                {
                    Found = false, Stage = "NotFound",
                    Note = "No data-plane input and no audited operation stand under this id."
                };
            return new AdminGetOperationStatusReply
            {
                Found = true, Stage = "Audited", Platform = audit.Command, Note = audit.DetailJson
            };
        }
        return new AdminGetOperationStatusReply
        {
            Found = true, Stage = OperationStage(lookup), AppliedTick = lookup.AppliedTick,
            Platform = lookup.Platform, Note = lookup.DeliveryState
        };
    }

    // The audit trail query (FIX-WEB-15): the control plane's own operation log, newest
    // first, on the viewer floor — the same read trust level as the single-id status
    // query above. Exact-word filters and an audit-id keyset cursor (append-only log, no
    // snapshot deadline); the store hands back one row more than asked for as the
    // has-more signal and the cursor is the last row's id.
    public override async Task<AdminAuditTrailReply> GetAuditTrail(
        AdminAuditTrailRequest request, ServerCallContext context)
    {
        if (request.MaxEntries is <= 0 or > 200)
            throw new RpcException(new Status(StatusCode.InvalidArgument,
                "The audit trail page is bounded (1..200)."));
        await RequireOperator(context, "viewer", "", "");
        long after = long.TryParse(request.PageToken,
            System.Globalization.CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
        var rows = await adminStore.QueryAuditTrailAsync(request.Command, request.Operator,
            request.MaxEntries + 1, after, context.CancellationToken);
        var reply = new AdminAuditTrailReply();
        foreach (var row in rows.Take(request.MaxEntries))
            reply.Entries.Add(new AdminAuditEntryProto
            {
                RequestId = row.RequestId, Operator = row.Operator, Reason = row.Reason,
                Scope = row.Scope, Command = row.Command, Detail = row.DetailJson,
                CreatedAtUnixMs = new DateTimeOffset(row.CreatedAtUtc).ToUnixTimeMilliseconds()
            });
        if (rows.Count > request.MaxEntries)
        {
            reply.HasMore = true;
            reply.NextPageToken = rows[request.MaxEntries - 1].AuditId.ToString(
                System.Globalization.CultureInfo.InvariantCulture);
        }
        return reply;
    }

    /// <summary>One input lookup translated into the operation's stage word: "Applied" only
    /// when a committed frame's input refs name the operation id; "Accepted" while the
    /// durable row stands unconsumed; a terminal disposition word (the room refused the
    /// input after dispatch) otherwise.</summary>
    private static string OperationStage(LookupInputReply lookup)
    {
        if (lookup.DeliveryState == "Consumed")
            return lookup.AppliedTick > 0 ? "Applied" : "Consumed";
        if (lookup.Disposition is not ("Routed" or ""))
            return lookup.Disposition;
        return "Accepted";
    }

    /// <summary>The literal "Duplicate" — RoomRuntime's disposition word for a replayed
    /// durable accept; kept as a literal because ControlServer does not reference GameServer.</summary>
    private const string DuplicateText = "Duplicate";

    // The back-office leaderboard queries (FIX-WEB-12): reads on the viewer floor that name
    // the board's read key — board, period bucket, scope and subject type — and ride the
    // RANK-04/05/06 query module. A stale or expired continuation token (RANK-06's snapshot
    // walk) is an explicit precondition error, never a cross-version seemingly-contiguous page.
    public override async Task<AdminLeaderboardTopReply> GetLeaderboardTop(
        AdminLeaderboardTopRequest request, ServerCallContext context)
    {
        if (string.IsNullOrWhiteSpace(request.TenantId) || string.IsNullOrWhiteSpace(request.GameId)
            || string.IsNullOrWhiteSpace(request.Board))
            throw new RpcException(new Status(StatusCode.InvalidArgument,
                "The leaderboard request is incomplete."));
        await RequireOperator(context, "viewer", request.TenantId, request.GameId);
        try
        {
            var page = await leaderboardQueries.GetTopPageAsync(request.TenantId,
                LeaderboardReadKey(request.TenantId, request.GameId, request.Board,
                    request.PeriodId, request.ScopeId, request.Subject),
                request.MaxEntries, request.PageToken, context.CancellationToken);
            var reply = new AdminLeaderboardTopReply
            {
                ReadVersionUnixMs = VersionUnixMs(page.ReadVersion),
                ReadVersion = FormatVersion(page.ReadVersion),
                HasMore = page.HasMore, NextPageToken = page.NextCursor ?? ""
            };
            foreach (var entry in page.Entries)
                reply.Entries.Add(new AdminLeaderboardEntryProto
                {
                    SubjectId = entry.SubjectId, Score = entry.Score
                });
            return reply;
        }
        catch (Exception error) when (error is ArgumentException or LeaderboardStaleReadException)
        {
            throw MapLeaderboardError(error);
        }
    }

    public override async Task<AdminLeaderboardRankReply> GetLeaderboardRank(
        AdminLeaderboardRankRequest request, ServerCallContext context)
    {
        if (string.IsNullOrWhiteSpace(request.TenantId) || string.IsNullOrWhiteSpace(request.GameId)
            || string.IsNullOrWhiteSpace(request.Board) || string.IsNullOrWhiteSpace(request.SubjectId))
            throw new RpcException(new Status(StatusCode.InvalidArgument,
                "The leaderboard request is incomplete."));
        await RequireOperator(context, "viewer", request.TenantId, request.GameId);
        try
        {
            var rank = await leaderboardQueries.GetRankAsync(request.TenantId,
                LeaderboardReadKey(request.TenantId, request.GameId, request.Board,
                    request.PeriodId, request.ScopeId, request.Subject),
                request.SubjectId, context.CancellationToken);
            if (rank is null)
                return new AdminLeaderboardRankReply { Found = false };
            return new AdminLeaderboardRankReply
            {
                Found = true, Rank = rank.Rank, Score = rank.Entry.Score,
                ReadVersionUnixMs = VersionUnixMs(rank.ReadVersion),
                ReadVersion = FormatVersion(rank.ReadVersion)
            };
        }
        catch (Exception error) when (error is ArgumentException or LeaderboardStaleReadException)
        {
            throw MapLeaderboardError(error);
        }
    }

    public override async Task<AdminLeaderboardAroundReply> GetLeaderboardAround(
        AdminLeaderboardAroundRequest request, ServerCallContext context)
    {
        if (string.IsNullOrWhiteSpace(request.TenantId) || string.IsNullOrWhiteSpace(request.GameId)
            || string.IsNullOrWhiteSpace(request.Board) || string.IsNullOrWhiteSpace(request.SubjectId))
            throw new RpcException(new Status(StatusCode.InvalidArgument,
                "The leaderboard request is incomplete."));
        await RequireOperator(context, "viewer", request.TenantId, request.GameId);
        try
        {
            var around = await leaderboardQueries.GetAroundMeAsync(request.TenantId,
                LeaderboardReadKey(request.TenantId, request.GameId, request.Board,
                    request.PeriodId, request.ScopeId, request.Subject),
                request.SubjectId, request.Window, context.CancellationToken);
            var reply = around is null
                ? new AdminLeaderboardAroundReply { Found = false }
                : new AdminLeaderboardAroundReply
                {
                    Found = true, ReadVersionUnixMs = VersionUnixMs(around.ReadVersion),
                    ReadVersion = FormatVersion(around.ReadVersion)
                };
            if (around is not null)
                foreach (var ranked in around.Entries)
                    reply.Entries.Add(new AdminLeaderboardRankedEntryProto
                    {
                        Rank = ranked.Rank, SubjectId = ranked.Entry.SubjectId, Score = ranked.Entry.Score
                    });
            return reply;
        }
        catch (Exception error) when (error is ArgumentException or LeaderboardStaleReadException)
        {
            throw MapLeaderboardError(error);
        }
    }

    // The leaderboard maintenance commands (FIX-WEB-14): the RANK-08 GM corrections on the
    // gm floor. The request leads with the audited triple (request id = idempotency key,
    // operator = the authenticated caller, reason = the audit evidence) and names the read
    // key exactly as the queries do; expected_version is the opaque read_version string a
    // query reply carried, and a board that changed since that read is refused before any
    // write (a precondition failure — HTTP 400 on the browser face — with the reload word,
    // never a correction onto a stale view).
    public override async Task<AdminLeaderboardAdjustReply> AdjustLeaderboardEntry(
        AdminLeaderboardAdjustRequest request, ServerCallContext context)
    {
        if (string.IsNullOrWhiteSpace(request.TenantId) || string.IsNullOrWhiteSpace(request.GameId)
            || string.IsNullOrWhiteSpace(request.Board) || string.IsNullOrWhiteSpace(request.SubjectId)
            || string.IsNullOrWhiteSpace(request.RequestId) || string.IsNullOrWhiteSpace(request.Reason))
            throw new RpcException(new Status(StatusCode.InvalidArgument,
                "The leaderboard adjustment request is incomplete."));
        var operatorIdentity = await RequireOperator(context, "gm", request.TenantId, request.GameId);
        if (request.Operator != operatorIdentity.OperatorId)
            throw new RpcException(new Status(StatusCode.PermissionDenied,
                "The request operator does not match the authenticated operator."));
        try
        {
            var result = await leaderboardQueries.AdjustEntryAsync(request.TenantId,
                LeaderboardReadKey(request.TenantId, request.GameId, request.Board,
                    request.PeriodId, request.ScopeId, request.Subject),
                request.SubjectId, request.Delta, request.ExpectedVersion,
                request.RequestId, request.Reason, context.CancellationToken);
            return new AdminLeaderboardAdjustReply
            {
                Score = result.Score, Duplicate = result.Duplicate,
                ReadVersionUnixMs = VersionUnixMs(result.Version), ReadVersion = FormatVersion(result.Version)
            };
        }
        catch (Exception error) when (error is ArgumentException or LeaderboardStaleReadException
            or LeaderboardRejectedException or LeaderboardIdempotencyConflictException)
        {
            throw MapLeaderboardError(error);
        }
    }

    public override async Task<AdminLeaderboardResetReply> ResetLeaderboardEntry(
        AdminLeaderboardResetRequest request, ServerCallContext context)
    {
        if (string.IsNullOrWhiteSpace(request.TenantId) || string.IsNullOrWhiteSpace(request.GameId)
            || string.IsNullOrWhiteSpace(request.Board)
            || string.IsNullOrWhiteSpace(request.RequestId) || string.IsNullOrWhiteSpace(request.Reason))
            throw new RpcException(new Status(StatusCode.InvalidArgument,
                "The leaderboard reset request is incomplete."));
        var operatorIdentity = await RequireOperator(context, "gm", request.TenantId, request.GameId);
        if (request.Operator != operatorIdentity.OperatorId)
            throw new RpcException(new Status(StatusCode.PermissionDenied,
                "The request operator does not match the authenticated operator."));
        try
        {
            // An empty subject id spells the whole-board reset (every subject on the bucket).
            var result = await leaderboardQueries.ResetEntryAsync(request.TenantId,
                LeaderboardReadKey(request.TenantId, request.GameId, request.Board,
                    request.PeriodId, request.ScopeId, request.Subject),
                request.SubjectId.Length > 0 ? request.SubjectId : null, request.ExpectedVersion,
                request.RequestId, request.Reason, context.CancellationToken);
            return new AdminLeaderboardResetReply
            {
                Duplicate = result.Duplicate,
                ReadVersionUnixMs = VersionUnixMs(result.Version), ReadVersion = FormatVersion(result.Version)
            };
        }
        catch (Exception error) when (error is ArgumentException or LeaderboardStaleReadException
            or LeaderboardRejectedException or LeaderboardIdempotencyConflictException)
        {
            throw MapLeaderboardError(error);
        }
    }

    public override async Task<AdminLeaderboardRebuildReply> RebuildLeaderboard(
        AdminLeaderboardRebuildRequest request, ServerCallContext context)
    {
        if (string.IsNullOrWhiteSpace(request.TenantId) || string.IsNullOrWhiteSpace(request.GameId)
            || string.IsNullOrWhiteSpace(request.Board)
            || string.IsNullOrWhiteSpace(request.RequestId) || string.IsNullOrWhiteSpace(request.Reason))
            throw new RpcException(new Status(StatusCode.InvalidArgument,
                "The leaderboard rebuild request is incomplete."));
        var operatorIdentity = await RequireOperator(context, "gm", request.TenantId, request.GameId);
        if (request.Operator != operatorIdentity.OperatorId)
            throw new RpcException(new Status(StatusCode.PermissionDenied,
                "The request operator does not match the authenticated operator."));
        try
        {
            var result = await leaderboardQueries.RebuildBoardAsync(request.TenantId,
                LeaderboardReadKey(request.TenantId, request.GameId, request.Board,
                    request.PeriodId, request.ScopeId, request.Subject),
                context.CancellationToken);
            return new AdminLeaderboardRebuildReply
            {
                Entries = result.Entries,
                ReadVersionUnixMs = VersionUnixMs(result.Version), ReadVersion = FormatVersion(result.Version)
            };
        }
        catch (Exception error) when (error is ArgumentException or LeaderboardStaleReadException
            or LeaderboardRejectedException or LeaderboardIdempotencyConflictException)
        {
            throw MapLeaderboardError(error);
        }
    }

    /// <summary>The board's read key (FIX-WEB-12): period bucket id (empty = the all-time
    /// bucket) and scope id (empty = the global scope) name the storage key; the subject
    /// type is the row filter. The definition-side descriptors (aggregation, bounds, the
    /// period's calendar) shape scoring, not queries — reads never consult them, so the
    /// descriptor carries neutral defaults rather than asking the operator to resupply
    /// what it cannot know.</summary>
    private static LeaderboardBoard LeaderboardReadKey(string tenantId, string gameId, string board,
        string periodId, string scopeId, int subject)
    {
        if (subject is < 0 or > (int)RankSubject.Faction)
            throw new ArgumentException("The subject type is not a RANK-01 subject type.");
        return new LeaderboardBoard(tenantId, gameId, board, (RankSubject)subject, RankAggregation.Sum,
            ScoreBounds.Default,
            new RankPeriod(RankPeriodKind.AllTime, periodId.Length > 0 ? periodId : "alltime",
                "", null, null, ""),
            new RankScope(scopeId.Length > 0 ? scopeId : "global"));
    }

    private static long VersionUnixMs(DateTimeOffset version) =>
        version == DateTimeOffset.MinValue ? 0 : version.ToUnixTimeMilliseconds();

    /// <summary>The read version in its opaque command-token form (FIX-WEB-14): the exact
    /// string a maintenance command's expected_version takes back — the round-trip "O"
    /// format, empty when the board has never scored. The unix-ms field is the display
    /// form; this one is lossless.</summary>
    private static string FormatVersion(DateTimeOffset version) =>
        version == DateTimeOffset.MinValue
            ? ""
            : version.ToString("O", System.Globalization.CultureInfo.InvariantCulture);

    private static RpcException MapLeaderboardError(Exception error)
    {
        // A stale snapshot (the board moved on or the walk's lifetime expired) and a
        // refused correction (bounds) are precondition failures — restart the read from
        // the first page or reload the board before retrying, never page or correct on.
        // A spent operation id is a conflict: the same id answered a different projection.
        return error switch
        {
            ArgumentException argument => new RpcException(new Status(StatusCode.InvalidArgument, argument.Message)),
            LeaderboardIdempotencyConflictException => new RpcException(
                new Status(StatusCode.AlreadyExists, error.Message)),
            _ => new RpcException(new Status(StatusCode.FailedPrecondition, error.Message)),
        };
    }

    public override async Task<AdminStreamerSnapshotReply> GetStreamerSnapshot(
        AdminStreamerSnapshotRequest request, ServerCallContext context)
    {
        if (string.IsNullOrWhiteSpace(request.TenantId) || string.IsNullOrWhiteSpace(request.StreamerId))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "The snapshot request is incomplete."));
        // The snapshot is a read (FIX-WEB-01): viewer floor, tenant scope from the request.
        await RequireOperator(context, "viewer", request.TenantId, "");
        var snapshot = await adminStore.GetStreamerSnapshotAsync(
            request.TenantId, request.StreamerId, context.CancellationToken);
        var reply = new AdminStreamerSnapshotReply
        {
            Found = snapshot.Found, StreamerId = snapshot.StreamerId, BanState = snapshot.BanState,
            CurrentSessionId = snapshot.CurrentSessionId ?? "", SessionVersion = snapshot.SessionVersion,
            TicketPresent = snapshot.Ticket is not null, OwnershipPresent = snapshot.Ownership is not null,
            OwnerInstanceId = snapshot.Ownership?.OwnerInstanceId ?? "",
            OwnershipEpoch = snapshot.Ownership?.Epoch ?? 0,
            OwnershipStatus = snapshot.Ownership?.Status ?? ""
        };
        foreach (var binding in snapshot.Bindings)
            reply.Bindings.Add(new AdminBindingProto
            {
                BindingId = binding.BindingId, GameId = binding.GameId, Generation = binding.Generation,
                State = binding.State, CurrentRoomId = binding.CurrentRoomId ?? "",
                CreatedUnixMs = new DateTimeOffset(binding.CreatedAtUtc).ToUnixTimeMilliseconds()
            });
        if (snapshot.Ticket is { } ticket)
            reply.Ticket = new TicketProto
            {
                TicketId = ticket.TicketId, State = ticket.State, PartitionKey = ticket.PartitionKey,
                ReservationId = ticket.ReservationId ?? "", Version = ticket.Version,
                StreamerId = snapshot.StreamerId, TenantId = request.TenantId
            };
        foreach (var task in snapshot.Tasks)
            reply.Tasks.Add(new AdminTaskProto
            {
                TaskId = task.TaskId, OperationId = task.OperationId, Status = task.Status,
                AttemptCount = task.AttemptCount, TaskEpoch = task.TaskEpoch
            });
        return reply;
    }

    public override async Task<AdminRoomSnapshotReply> GetRoomSnapshot(
        AdminRoomSnapshotRequest request, ServerCallContext context)
    {
        if (string.IsNullOrWhiteSpace(request.RoomId))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "The snapshot request is incomplete."));
        // The snapshot is a read (FIX-WEB-01): viewer floor, scope resolved from the room —
        // the request carries only the room id, so the room's own tenant/game is the target.
        var identity = await RequireOperator(context, "viewer", "", "");
        var snapshot = await adminStore.GetRoomSnapshotAsync(request.RoomId, context.CancellationToken);
        if (snapshot.Found && !OperatorAuthorizer.InScope(identity, snapshot.TenantId, snapshot.GameId))
            throw new RpcException(new Status(StatusCode.PermissionDenied,
                "Operator scope does not cover the target room."));
        return new AdminRoomSnapshotReply
        {
            Found = snapshot.Found, TenantId = snapshot.TenantId, GameId = snapshot.GameId,
            Status = snapshot.Status, Epoch = snapshot.Epoch, LastTick = snapshot.LastTick,
            StateVersion = snapshot.StateVersion, OwnershipPresent = snapshot.Ownership is not null,
            OwnerInstanceId = snapshot.Ownership?.OwnerInstanceId ?? "",
            OwnershipEpoch = snapshot.Ownership?.Epoch ?? 0,
            OwnershipStatus = snapshot.Ownership?.Status ?? "",
            Participants = { snapshot.Participants.Select(participant => new AdminParticipantProto
            {
                ParticipantId = participant.ParticipantId, BindingId = participant.BindingId ?? "",
                Role = participant.Role, State = participant.State
            }) },
            ResultVersion = snapshot.ResultVersion, FinalFrame = snapshot.FinalFrame,
            GrantsTotal = snapshot.GrantsTotal, GrantsGranted = snapshot.GrantsGranted,
            SettleStatus = snapshot.SettleTask?.Status ?? "",
            SettleAttempts = snapshot.SettleTask?.AttemptCount ?? 0,
            SettleLastError = snapshot.SettleTask?.LastError ?? "",
            SettleNextAttemptUnixMs = snapshot.SettleTask?.NextAttempt is { } next
                ? new DateTimeOffset(next).ToUnixTimeMilliseconds() : 0,
            SettleOwnerInstanceId = snapshot.SettleTask?.OwnerInstanceId ?? "",
            ReleaseBlockedReason = snapshot.ReleaseBlockedReason ?? "",
            ReleaseBlockedSinceUnixMs = snapshot.ReleaseBlockedSince is { } since
                ? new DateTimeOffset(since).ToUnixTimeMilliseconds() : 0,
            ReleaseBlockedTotalMs = snapshot.ReleaseBlockedTotalMs
        };
    }

    public override async Task<AdminTraceRoundReply> TraceRound(
        AdminTraceRoundRequest request, ServerCallContext context)
    {
        if (string.IsNullOrWhiteSpace(request.RoomId))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "The trace request is incomplete."));
        // The trace is a read (FIX-WEB-01): viewer floor, scope resolved from the room (the
        // request carries only the room id), same shape as GetRoomSnapshot above.
        var identity = await RequireOperator(context, "viewer", "", "");
        var scope = await adminStore.GetRoomSnapshotAsync(request.RoomId, context.CancellationToken);
        if (scope.Found && !OperatorAuthorizer.InScope(identity, scope.TenantId, scope.GameId))
            throw new RpcException(new Status(StatusCode.PermissionDenied,
                "Operator scope does not cover the target room."));
        var trace = await adminStore.TraceRoundAsync(request.RoomId, context.CancellationToken);
        return new AdminTraceRoundReply
        {
            Found = trace.Found, Status = trace.Status, Epoch = trace.Epoch, LastTick = trace.LastTick,
            Events = { trace.Events.Select(item => new AdminTraceEventProto
            {
                Sequence = item.Sequence, EventId = item.EventId, Disposition = item.Disposition,
                BindingId = item.BindingId ?? "",
                ReceivedUnixMs = new DateTimeOffset(item.ReceivedAtUtc).ToUnixTimeMilliseconds(),
                PayloadJson = item.PayloadJson
            }) },
            Frames = { trace.Frames.Select(item => new AdminTraceFrameProto
            {
                Tick = item.Tick, CommitId = item.CommitId, InputRefsJson = item.InputRefsJson,
                EffectsJson = item.EffectsJson
            }) },
            Effects = { trace.Effects.Select(item => new AdminTraceEffectProto
            {
                EffectId = item.EffectId, Kind = item.Kind, Status = item.Status, PayloadJson = item.PayloadJson
            }) },
            ResultVersion = trace.ResultVersion, ResultJson = trace.ResultJson,
            Grants = { trace.Grants.Select(item => new AdminTraceGrantProto
            {
                PlayerId = item.PlayerId, GrantKey = item.GrantKey, Status = item.Status,
                OperationId = item.OperationId
            }) },
            Ledger = { trace.Ledger.Select(item => new AdminTraceLedgerProto
            {
                OperationId = item.OperationId, LineId = item.LineId, PlayerId = item.PlayerId,
                AssetId = item.AssetId, Delta = item.Delta, Reason = item.Reason,
                ResultingBalance = item.ResultingBalance,
                CommittedUnixMs = new DateTimeOffset(item.CommittedAtUtc).ToUnixTimeMilliseconds()
            }) }
        };
    }

    public override async Task<AdminQueueGateReply> SuspendMatchQueue(
        AdminQueueGateRequest request, ServerCallContext context) =>
        await QueueGateAsync(request, context, suspended: true, "SuspendMatchQueue");

    public override async Task<AdminQueueGateReply> ResumeMatchQueue(
        AdminQueueGateRequest request, ServerCallContext context) =>
        await QueueGateAsync(request, context, suspended: false, "ResumeMatchQueue");

    private async Task<AdminQueueGateReply> QueueGateAsync(
        AdminQueueGateRequest request, ServerCallContext context, bool suspended, string command)
    {
        if (Incomplete(request.RequestId, request.Operator, request.Reason) ||
            string.IsNullOrWhiteSpace(request.PartitionKey))
            throw new RpcException(new Status(StatusCode.InvalidArgument,
                "The command needs a request id, an operator, a reason and a partition."));
        // The gate command is gm (FIX-WEB-01) and cluster-wide — the partition key names no
        // tenant/game, so the role floor is the whole gate here.
        var operatorIdentity = await RequireOperator(context, "gm", "", "");
        if (request.Operator != operatorIdentity.OperatorId)
            throw new RpcException(new Status(StatusCode.PermissionDenied,
                "The request operator does not match the authenticated operator."));
        var acted = await adminStore.WriteAuditAsync(request.RequestId, request.Operator, request.Reason,
            request.PartitionKey, command, $$"""{"partition":"{{request.PartitionKey}}","suspended":{{(suspended ? "true" : "false")}}}""",
            context.CancellationToken);
        if (!acted)
        {
            // A replayed command reports the gate as it stands now — a later resume must not be
            // undone by a duplicate of an earlier suspend.
            var current = await adminStore.GetQueueGateAsync(request.PartitionKey, context.CancellationToken);
            return new AdminQueueGateReply
            {
                PartitionKey = request.PartitionKey,
                Suspended = current?.Suspended ?? !suspended, Duplicate = true
            };
        }
        var gate = await adminStore.SetQueueGateAsync(
            request.PartitionKey, suspended, request.Operator, context.CancellationToken);
        return new AdminQueueGateReply
        {
            PartitionKey = gate.PartitionKey, Suspended = gate.Suspended, Duplicate = false
        };
    }

    public override async Task<AdminRoomRecoveryReply> RetryRoomRecovery(
        AdminRoomRecoveryRequest request, ServerCallContext context)
    {
        if (Incomplete(request.RequestId, request.Operator, request.Reason) ||
            string.IsNullOrWhiteSpace(request.TenantId) || string.IsNullOrWhiteSpace(request.GameId) ||
            string.IsNullOrWhiteSpace(request.RoomId) || string.IsNullOrWhiteSpace(request.ToInstanceId) ||
            request.TickMs <= 0)
            throw new RpcException(new Status(StatusCode.InvalidArgument, "The recovery request is incomplete."));
        // The recovery command is gm (FIX-WEB-01), scoped by the request's own tenant/game.
        var operatorIdentity = await RequireOperator(context, "gm", request.TenantId, request.GameId);
        if (request.Operator != operatorIdentity.OperatorId)
            throw new RpcException(new Status(StatusCode.PermissionDenied,
                "The request operator does not match the authenticated operator."));
        var acted = await adminStore.WriteAuditAsync(request.RequestId, request.Operator, request.Reason,
            request.RoomId, "RetryRoomRecovery",
            $$"""{"room":"{{request.RoomId}}","to":"{{request.ToInstanceId}}"}""", context.CancellationToken);
        if (!acted)
            return new AdminRoomRecoveryReply { Duplicate = true };
        // The one barriered recovery flow (FIX-REC-01): the named instance replays the
        // round under the Recovering fence, holds at the pinned head, the gateways confirm
        // the route, and only then the fenced grant activates the loop — a failed recovery
        // or an unconfirmed barrier leaves the ownership Recovering, never a ready room
        // nothing runs.
        var recovered = await _takeover.RecoverRoomWithBarrierAsync(request.TenantId, request.GameId,
            request.RoomId, request.ToInstanceId, request.TickMs, context.CancellationToken);
        return new AdminRoomRecoveryReply
        {
            OwnerInstanceId = recovered.OwnerInstanceId, Epoch = recovered.Epoch,
            Recovered = recovered.Recovered, Status = recovered.Status, LastTick = recovered.LastTick
        };
    }

    public override async Task<AdminDrainProcessReply> DrainProcess(
        AdminDrainProcessRequest request, ServerCallContext context)
    {
        if (Incomplete(request.RequestId, request.Operator, request.Reason) ||
            string.IsNullOrWhiteSpace(request.ProcessInstanceId))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "The drain request is incomplete."));
        // The drain command is gm (FIX-WEB-01) and cluster-wide — a process instance spans
        // tenants, so no tenant/game scope is derivable; the role floor is the whole gate.
        var operatorIdentity = await RequireOperator(context, "gm", "", "");
        if (request.Operator != operatorIdentity.OperatorId)
            throw new RpcException(new Status(StatusCode.PermissionDenied,
                "The request operator does not match the authenticated operator."));
        var acted = await adminStore.WriteAuditAsync(request.RequestId, request.Operator, request.Reason,
            request.ProcessInstanceId, "DrainProcess",
            $$"""{"process":"{{request.ProcessInstanceId}}"}""", context.CancellationToken);
        if (!acted)
        {
            var currentAdmission = await adminStore.ReadProcessAdmissionAsync(
                request.ProcessInstanceId, context.CancellationToken);
            return new AdminDrainProcessReply
            {
                ProcessInstanceId = request.ProcessInstanceId, Found = currentAdmission is not null,
                Lifecycle = currentAdmission ?? "", Duplicate = true
            };
        }
        var lifecycle = await adminStore.DrainProcessAsync(request.ProcessInstanceId, context.CancellationToken)
            ?? throw new RpcException(new Status(StatusCode.NotFound,
                $"The process '{request.ProcessInstanceId}' is not registered."));
        return new AdminDrainProcessReply
        {
            ProcessInstanceId = request.ProcessInstanceId, Found = true, Lifecycle = lifecycle
        };
    }

    public override async Task<AdminSettlementRetryReply> RetrySettlement(
        AdminSettlementRetryRequest request, ServerCallContext context)
    {
        if (Incomplete(request.RequestId, request.Operator, request.Reason) ||
            string.IsNullOrWhiteSpace(request.RoomId))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "The retry request is incomplete."));
        // The retry command is gm (FIX-WEB-01), scoped by the room's own tenant/game (the
        // request carries only the room id) — same shape as the room-scoped reads above.
        var operatorIdentity = await RequireOperator(context, "gm", "", "");
        if (request.Operator != operatorIdentity.OperatorId)
            throw new RpcException(new Status(StatusCode.PermissionDenied,
                "The request operator does not match the authenticated operator."));
        var room = await adminStore.GetRoomSnapshotAsync(request.RoomId, context.CancellationToken);
        if (!room.Found)
            throw new RpcException(new Status(StatusCode.NotFound, "The room does not exist."));
        if (!OperatorAuthorizer.InScope(operatorIdentity, room.TenantId, room.GameId))
            throw new RpcException(new Status(StatusCode.PermissionDenied,
                "Operator scope does not cover the target room."));
        var acted = await adminStore.WriteAuditAsync(request.RequestId, request.Operator, request.Reason,
            request.RoomId, "RetrySettlement",
            $$"""{"room":"{{request.RoomId}}","tenant":"{{room.TenantId}}","game":"{{room.GameId}}"}""",
            context.CancellationToken);
        if (!acted)
            return new AdminSettlementRetryReply
            {
                RoomId = request.RoomId, ResultVersion = room.ResultVersion, GrantsTotal = room.GrantsTotal,
                GrantsGranted = room.GrantsGranted, GrantsPending = room.GrantsTotal - room.GrantsGranted,
                Duplicate = true
            };
        // CommitResult is itself idempotent per grant receipt: already-granted lines replay as
        // zero pending work, and only the incomplete grants are retried. A returned call means
        // the grants now all stand applied (a fault throws FailedPrecondition), so the
        // executor's settle_task — if the failure it records was ever the reason for this
        // repair — is obsolete and leaves the table (FIX-SETTLE-03).
        var retry = await _settlement.CommitResultAsync(new CommitResultRequest
        {
            RoomId = request.RoomId, GameId = room.GameId, TenantId = room.TenantId,
            OperationId = "admin:" + request.RequestId
        }, deadline: DateTime.UtcNow.Add(CallTimeout), cancellationToken: context.CancellationToken);
        await controlStore.CompleteSettleTaskAsync(request.RoomId, context.CancellationToken);
        return new AdminSettlementRetryReply
        {
            RoomId = retry.RoomId, ResultVersion = retry.ResultVersion, GrantsTotal = retry.GrantsTotal,
            GrantsGranted = retry.GrantsGranted, GrantsPending = retry.GrantsPending
        };
    }

    public override async Task<AdminTaskReconcileReply> ReconcilePlatformTask(
        AdminTaskReconcileRequest request, ServerCallContext context)
    {
        if (Incomplete(request.RequestId, request.Operator, request.Reason) ||
            string.IsNullOrWhiteSpace(request.TaskId))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "The reconcile request is incomplete."));
        // The reconcile command is gm (FIX-WEB-01) and cluster-wide — a platform task names no
        // tenant/game, so the role floor is the whole gate.
        var operatorIdentity = await RequireOperator(context, "gm", "", "");
        if (request.Operator != operatorIdentity.OperatorId)
            throw new RpcException(new Status(StatusCode.PermissionDenied,
                "The request operator does not match the authenticated operator."));
        var acted = await adminStore.WriteAuditAsync(request.RequestId, request.Operator, request.Reason,
            request.TaskId, "ReconcilePlatformTask",
            $$"""{"task":"{{request.TaskId}}"}""", context.CancellationToken);
        if (!acted)
            return new AdminTaskReconcileReply { TaskId = request.TaskId, Duplicate = true };
        // Only a NeedsAttention task requeues — the audited decision to try again, then the
        // executors pick it up through the normal lease path.
        var task = await _tasks.RequeueTaskAsync(new TaskRequeueRequest { TaskId = request.TaskId },
            deadline: DateTime.UtcNow.Add(CallTimeout), cancellationToken: context.CancellationToken);
        return new AdminTaskReconcileReply
        {
            TaskId = task.TaskId, Status = task.Status, TaskEpoch = task.TaskEpoch
        };
    }

    private static bool Incomplete(string requestId, string operation, string reason) =>
        string.IsNullOrWhiteSpace(requestId) || string.IsNullOrWhiteSpace(operation) ||
        string.IsNullOrWhiteSpace(reason);

    /// <summary>Authenticates a human operator from the bearer credential in the request
    /// metadata and authorizes the role and the target tenant/game scope (FIX-GM-06). A
    /// process certificate never satisfies this gate — the operator identity exists only in
    /// the operator store, so an internal process cannot impersonate an operator.</summary>
    private async Task<OperatorIdentity> RequireOperator(ServerCallContext context, string requiredRole,
        string tenantId, string gameId)
    {
        var token = ReadOperatorToken(context);
        if (token is null)
            throw new RpcException(new Status(StatusCode.Unauthenticated, "Operator credentials are absent."));
        var operatorIdentity = await operatorStore.ResolveOperatorAsync(token, context.CancellationToken);
        if (operatorIdentity is null)
            throw new RpcException(new Status(StatusCode.Unauthenticated, "Operator credential is invalid."));
        if (!OperatorAuthorizer.HasRole(operatorIdentity.Role, requiredRole))
            throw new RpcException(new Status(StatusCode.PermissionDenied, "Operator role is not authorized."));
        if (!OperatorAuthorizer.InScope(operatorIdentity, tenantId, gameId))
            throw new RpcException(new Status(StatusCode.PermissionDenied,
                "Operator scope does not cover the target."));
        return operatorIdentity;
    }

    /// <summary>Reads the operator bearer token from the gRPC metadata: the standard
    /// "Authorization: Bearer &lt;token&gt;" header or the explicit "x-operator-token" header.</summary>
    private static string? ReadOperatorToken(ServerCallContext context)
    {
        var authorization = context.RequestHeaders.FirstOrDefault(header =>
            header.Key.Equals("authorization", StringComparison.OrdinalIgnoreCase))?.Value;
        if (authorization is not null &&
            authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return authorization["Bearer ".Length..];
        return context.RequestHeaders.FirstOrDefault(header =>
            header.Key.Equals("x-operator-token", StringComparison.OrdinalIgnoreCase))?.Value;
    }

    private ProcessIdentity Require(ServerCallContext context, params string[] roles)
    {
        var certificate = context.GetHttpContext().Connection.ClientCertificate;
        if (certificate is null)
            throw new RpcException(new Status(StatusCode.Unauthenticated, "Client certificate is absent from request context."));
        var caller = trustedCertificates.Resolve(certificate);
        if (caller is null)
            throw new RpcException(new Status(StatusCode.Unauthenticated, "Client certificate identity is invalid."));
        if (!roles.Contains(caller.Role, StringComparer.Ordinal))
            throw new RpcException(new Status(StatusCode.PermissionDenied, "Role is not authorized."));
        return caller;
    }
}

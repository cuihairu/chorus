using System.Globalization;
using Chorus.Rpc;
using Grpc.Core;

namespace Chorus.ControlServer;

/// <summary>
/// FIX-RANK-04: the data-plane face of the leaderboard query business module. The
/// production implementation rides the <see cref="Leaderboard.LeaderboardClient"/> (the data
/// host's gRPC face); tests fake it, so the module's board validation and error mapping are
/// unit-testable without a cluster.
/// </summary>
public interface ILeaderboardQueryDataPlane
{
    Task<TopNReply> GetTopAsync(GetTopRequest request, CancellationToken cancellationToken = default);

    Task<RankReply> GetRankAsync(GetRankRequest request, CancellationToken cancellationToken = default);

    Task<AroundReply> GetAroundMeAsync(GetAroundMeRequest request, CancellationToken cancellationToken = default);

    Task<AdjustBoardReply> AdjustBoardAsync(AdjustBoardRequest request, CancellationToken cancellationToken = default);

    Task<ResetBoardReply> ResetBoardAsync(ResetBoardRequest request, CancellationToken cancellationToken = default);

    Task<RebuildBoardReply> RebuildBoardAsync(RebuildBoardRequest request, CancellationToken cancellationToken = default);
}

/// <summary>The production data-plane implementation: the data host's leaderboard face.</summary>
public sealed class GrpcLeaderboardQueryDataPlane(Leaderboard.LeaderboardClient client)
    : ILeaderboardQueryDataPlane
{
    public Task<TopNReply> GetTopAsync(GetTopRequest request, CancellationToken cancellationToken = default) =>
        client.GetTopAsync(request, cancellationToken: cancellationToken).ResponseAsync;

    public Task<RankReply> GetRankAsync(GetRankRequest request, CancellationToken cancellationToken = default) =>
        client.GetRankAsync(request, cancellationToken: cancellationToken).ResponseAsync;

    public Task<AroundReply> GetAroundMeAsync(GetAroundMeRequest request, CancellationToken cancellationToken = default) =>
        client.GetAroundMeAsync(request, cancellationToken: cancellationToken).ResponseAsync;

    public Task<AdjustBoardReply> AdjustBoardAsync(AdjustBoardRequest request, CancellationToken cancellationToken = default) =>
        client.AdjustBoardAsync(request, cancellationToken: cancellationToken).ResponseAsync;

    public Task<ResetBoardReply> ResetBoardAsync(ResetBoardRequest request, CancellationToken cancellationToken = default) =>
        client.ResetBoardAsync(request, cancellationToken: cancellationToken).ResponseAsync;

    public Task<RebuildBoardReply> RebuildBoardAsync(RebuildBoardRequest request, CancellationToken cancellationToken = default) =>
        client.RebuildBoardAsync(request, cancellationToken: cancellationToken).ResponseAsync;
}

/// <summary>
/// FIX-RANK-04: the ControlServer business module that answers a board's bounded TopN
/// (developer-capabilities.md「查询」「TopN、个人名次、附近名次；有限页大小；分页绑定榜单读取版本」).
/// The caller names the board (the RANK-01 definition) and the page size; this module validates
/// the request (a non-empty tenant, a well-formed board, a bounded page) and asks the data
/// plane to read. The answer carries the board's read version — a paginated continuation
/// passes it back and a changed board is a stale read, never a cross-version page. The
/// data plane re-authorizes by role and tenant (identity), so this module selects and
/// validates; it never bypasses the storage floor.
/// </summary>
public sealed class LeaderboardQueryService(ILeaderboardQueryDataPlane dataPlane)
{
    /// <summary>Answers the bounded TopN of one board in its declared order. The caller names
    /// the tenant, the board definition and the page size; <paramref name="expectedVersion"/>
    /// (optional) is the read version the caller's page was bound to — a mismatch is a
    /// <see cref="LeaderboardStaleReadException"/>, the caller must restart the read.</summary>
    public async Task<LeaderboardTopResult> GetTopAsync(string tenantId, LeaderboardBoard board,
        int maxEntries, string? expectedVersion = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
            throw new ArgumentException("A tenant id must not be empty.", nameof(tenantId));
        board.Validate();
        if (maxEntries is <= 0 or > LeaderboardQueryLimits.MaxTopEntries)
            throw new ArgumentOutOfRangeException(nameof(maxEntries), "A query page is bounded.");
        try
        {
            var reply = await dataPlane.GetTopAsync(new GetTopRequest
            {
                TenantId = tenantId, GameId = board.GameId,
                Board = LeaderboardScoringService.ToProto(board), MaxEntries = maxEntries,
                ExpectedVersion = expectedVersion ?? ""
            }, cancellationToken);
            var entries = reply.Entries.Select(entry =>
                new RankEntry((RankSubject)entry.Subject, entry.SubjectId, entry.Score)).ToList();
            return new LeaderboardTopResult(entries, ParseVersion(reply.ReadVersion));
        }
        catch (RpcException error) when (error.StatusCode == StatusCode.FailedPrecondition)
        {
            throw new LeaderboardStaleReadException(error.Status.Detail);
        }
        catch (RpcException error) when (error.StatusCode == StatusCode.InvalidArgument)
        {
            throw new ArgumentException(error.Status.Detail);
        }
    }

    /// <summary>Answers one page of a paginated TopN walk (developer-capabilities.md「稳定分页」
    /// 「分页实现可返回有期限的快照版本/游标」). <paramref name="cursor"/> is the continuation
    /// cursor the previous page carried (null on a first page); the answer carries the next
    /// page's cursor exactly when more entries remain. A continuation is valid only while the
    /// board's write version is unchanged and the snapshot lifetime (<see
    /// cref="LeaderboardQueryLimits.SnapshotTtlSeconds"/>) has not expired — otherwise a
    /// <see cref="LeaderboardStaleReadException"/> (an explicit error, never a cross-version
    /// seemingly-contiguous page). The data plane re-authorizes by role and tenant.</summary>
    public async Task<LeaderboardPageResult> GetTopPageAsync(string tenantId, LeaderboardBoard board,
        int maxEntries, string? cursor = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
            throw new ArgumentException("A tenant id must not be empty.", nameof(tenantId));
        board.Validate();
        if (maxEntries is <= 0 or > LeaderboardQueryLimits.MaxTopEntries)
            throw new ArgumentOutOfRangeException(nameof(maxEntries), "A query page is bounded.");
        try
        {
            var reply = await dataPlane.GetTopAsync(new GetTopRequest
            {
                TenantId = tenantId, GameId = board.GameId,
                Board = LeaderboardScoringService.ToProto(board), MaxEntries = maxEntries,
                Cursor = cursor ?? ""
            }, cancellationToken);
            var entries = reply.Entries.Select(entry =>
                new RankEntry((RankSubject)entry.Subject, entry.SubjectId, entry.Score)).ToList();
            var nextCursor = reply.NextCursor.Length > 0 ? reply.NextCursor : null;
            return new LeaderboardPageResult(entries, ParseVersion(reply.ReadVersion),
                nextCursor is not null, nextCursor);
        }
        catch (RpcException error) when (error.StatusCode == StatusCode.FailedPrecondition)
        {
            throw new LeaderboardStaleReadException(error.Status.Detail);
        }
        catch (RpcException error) when (error.StatusCode == StatusCode.InvalidArgument)
        {
            throw new ArgumentException(error.Status.Detail);
        }
    }

    /// <summary>Answers one subject's position on a board — its score, its unique ordinal
    /// rank and the board's read version, at one read view (developer-capabilities.md
    /// 「实时变化榜的个人名次必须对应同一次读取视图」). The caller names the tenant, the board and
    /// the subject; the rank is the board's declared order. Answers null when the subject has
    /// not scored on the board.</summary>
    public async Task<LeaderboardRankResult?> GetRankAsync(string tenantId, LeaderboardBoard board,
        string subjectId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
            throw new ArgumentException("A tenant id must not be empty.", nameof(tenantId));
        if (string.IsNullOrWhiteSpace(subjectId))
            throw new ArgumentException("A subject id must not be empty.", nameof(subjectId));
        board.Validate();
        try
        {
            var reply = await dataPlane.GetRankAsync(new GetRankRequest
            {
                TenantId = tenantId, GameId = board.GameId,
                Board = LeaderboardScoringService.ToProto(board), SubjectId = subjectId
            }, cancellationToken);
            if (!reply.Found) return null;
            return new LeaderboardRankResult(reply.Rank,
                new RankEntry((RankSubject)reply.Entry.Subject, reply.Entry.SubjectId, reply.Entry.Score),
                ParseVersion(reply.ReadVersion));
        }
        catch (RpcException error) when (error.StatusCode == StatusCode.InvalidArgument)
        {
            throw new ArgumentException(error.Status.Detail);
        }
    }

    /// <summary>Answers the entries within a bounded window of a subject's rank (the subject
    /// included), in rank order, at one read view (developer-capabilities.md「个人名次、附近名次」).
    /// The caller names the tenant, the board, the subject and the window size; the window is
    /// bounded (<see cref="LeaderboardQueryLimits.MaxAroundWindow"/>). Answers null when the
    /// subject has not scored on the board.</summary>
    public async Task<LeaderboardAroundResult?> GetAroundMeAsync(string tenantId, LeaderboardBoard board,
        string subjectId, int window, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
            throw new ArgumentException("A tenant id must not be empty.", nameof(tenantId));
        if (string.IsNullOrWhiteSpace(subjectId))
            throw new ArgumentException("A subject id must not be empty.", nameof(subjectId));
        board.Validate();
        if (window is <= 0 or > LeaderboardQueryLimits.MaxAroundWindow)
            throw new ArgumentOutOfRangeException(nameof(window), "An around-me window is bounded.");
        try
        {
            var reply = await dataPlane.GetAroundMeAsync(new GetAroundMeRequest
            {
                TenantId = tenantId, GameId = board.GameId,
                Board = LeaderboardScoringService.ToProto(board), SubjectId = subjectId, Window = window
            }, cancellationToken);
            if (!reply.Found) return null;
            var entries = reply.Entries.Select(entry =>
                new RankedEntry(entry.Rank,
                    new RankEntry((RankSubject)entry.Entry.Subject, entry.Entry.SubjectId, entry.Entry.Score)))
                .ToList();
            return new LeaderboardAroundResult(entries, ParseVersion(reply.ReadVersion));
        }
        catch (RpcException error) when (error.StatusCode == StatusCode.InvalidArgument)
        {
            throw new ArgumentException(error.Status.Detail);
        }
    }

    private static DateTimeOffset ParseVersion(string value) =>
        value.Length == 0
            ? DateTimeOffset.MinValue
            : DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    /// <summary>FIX-WEB-14: applies one GM correction (RANK-08) through the data plane.
    /// The operation id is the idempotency key and the audit anchor — a replay answers the
    /// stored score as a duplicate, the same id with a different projection is a visible
    /// conflict; expectedVersion is the opaque read version the operator last saw (empty
    /// = no guard) and a changed board is a stale read. Returns the resulting score and
    /// the board's version after the correction.</summary>
    public async Task<LeaderboardAdjustResult> AdjustEntryAsync(string tenantId, LeaderboardBoard board,
        string subjectId, long delta, string? expectedVersion, string operationId, string reason,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
            throw new ArgumentException("A tenant id must not be empty.", nameof(tenantId));
        if (string.IsNullOrWhiteSpace(operationId))
            throw new ArgumentException("A GM correction needs its operation id (the idempotency and audit anchor).", nameof(operationId));
        board.Validate();
        try
        {
            var reply = await dataPlane.AdjustBoardAsync(new AdjustBoardRequest
            {
                TenantId = tenantId, GameId = board.GameId,
                Board = LeaderboardScoringService.ToProto(board), SubjectId = subjectId,
                Delta = delta, ExpectedVersion = expectedVersion ?? "",
                OperationId = operationId, Reason = reason ?? ""
            }, cancellationToken);
            return new LeaderboardAdjustResult(reply.Score, reply.Duplicate, ParseVersion(reply.Version));
        }
        catch (RpcException error)
        {
            throw MapCommandError(error);
        }
    }

    /// <summary>FIX-WEB-14: applies one GM reset (RANK-08) through the data plane — zeroing
    /// one subject's entry, or the whole board's bucket when <paramref name="subjectId"/>
    /// is null. The same idempotency, version guard and evidence as the correction.</summary>
    public async Task<LeaderboardResetResult> ResetEntryAsync(string tenantId, LeaderboardBoard board,
        string? subjectId, string? expectedVersion, string operationId, string reason,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
            throw new ArgumentException("A tenant id must not be empty.", nameof(tenantId));
        if (string.IsNullOrWhiteSpace(operationId))
            throw new ArgumentException("A GM reset needs its operation id (the idempotency and audit anchor).", nameof(operationId));
        board.Validate();
        try
        {
            var reply = await dataPlane.ResetBoardAsync(new ResetBoardRequest
            {
                TenantId = tenantId, GameId = board.GameId,
                Board = LeaderboardScoringService.ToProto(board), SubjectId = subjectId ?? "",
                ExpectedVersion = expectedVersion ?? "",
                OperationId = operationId, Reason = reason ?? ""
            }, cancellationToken);
            return new LeaderboardResetResult(reply.Duplicate, ParseVersion(reply.Version));
        }
        catch (RpcException error)
        {
            throw MapCommandError(error);
        }
    }

    /// <summary>FIX-WEB-14: rebuilds one board's projection from its effect ledger through
    /// the data plane (RANK-08) — the recovery move when the projection itself is in doubt.
    /// It names no expected version (the entries are what is in doubt) and never touches
    /// the committed outbox rows; the ledger's scoring evidence stays intact. Returns the
    /// number of re-derived entries and the board's version at the same snapshot.</summary>
    public async Task<LeaderboardRebuildResult> RebuildBoardAsync(string tenantId, LeaderboardBoard board,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
            throw new ArgumentException("A tenant id must not be empty.", nameof(tenantId));
        board.Validate();
        try
        {
            var reply = await dataPlane.RebuildBoardAsync(new RebuildBoardRequest
            {
                TenantId = tenantId, GameId = board.GameId,
                Board = LeaderboardScoringService.ToProto(board)
            }, cancellationToken);
            return new LeaderboardRebuildResult(reply.Entries, ParseVersion(reply.Version));
        }
        catch (RpcException error)
        {
            throw MapCommandError(error);
        }
    }

    /// <summary>The data plane's command-face failure words (FIX-RANK-08's gRPC mapping):
    /// aborted = the board changed since the operator's read version (a stale read),
    /// failed-precondition = the correction was refused before any write (bounds),
    /// already-exists = the operation id was spent on a different projection,
    /// invalid-argument = a malformed request. Never a silent re-amount.</summary>
    private static Exception MapCommandError(RpcException error) => error.StatusCode switch
    {
        StatusCode.Aborted => new LeaderboardStaleReadException(error.Status.Detail),
        StatusCode.FailedPrecondition => new LeaderboardRejectedException(error.Status.Detail),
        StatusCode.AlreadyExists => new LeaderboardIdempotencyConflictException(error.Status.Detail),
        StatusCode.InvalidArgument => new ArgumentException(error.Status.Detail),
        _ => error,
    };
}

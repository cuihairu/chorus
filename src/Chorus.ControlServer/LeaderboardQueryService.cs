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
}

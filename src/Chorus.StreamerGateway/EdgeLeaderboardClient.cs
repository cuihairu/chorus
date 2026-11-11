using Chorus.Rpc;
using Chorus.StreamerGateway.Messages;
using Grpc.Core;
using GrpcChannel = Grpc.Net.Client.GrpcChannel;

namespace Chorus.StreamerGateway;

/// <summary>The leaderboard read boundary (FIX-U3D-13): the data-plane queries behind the
/// edge's leaderboardQuery lane. The client is a thin caller — it names the board's read
/// key (the same neutral-descriptor synthesis the back office reads through: reads key on
/// identity, and the write side's aggregation, bounds and period calendar shape scoring,
/// not queries), rides the RANK-04/05/05 query RPCs, and surfaces the two words a client
/// can act on: a stale continuation (restart the walk) and a malformed key (the protocol's
/// invalid-message refusal). Everything else is the internal error word.</summary>
public sealed class EdgeLeaderboardClient(Func<GrpcChannel> channelFactory)
{
    private readonly Lazy<GrpcChannel> _channel = new(channelFactory);

    /// <summary>One bounded page of a board's TopN walk (RANK-04/06). The cursor rides the
    /// previous page's next-cursor (empty or null starts a walk); a continuation whose
    /// board changed or whose snapshot expired throws <see cref="EdgeLeaderboardStaleException"/>.</summary>
    public async Task<EdgeLeaderboardPage> GetTopPageAsync(string tenantId, string gameId,
        EdgeLeaderboardQuery query, CancellationToken cancellation)
    {
        var client = new Leaderboard.LeaderboardClient(_channel.Value);
        try
        {
            var reply = await client.GetTopAsync(new GetTopRequest
            {
                TenantId = tenantId,
                GameId = gameId,
                Board = ReadKey(gameId, query),
                MaxEntries = query.MaxEntries,
                Cursor = query.Cursor ?? string.Empty,
            }, cancellationToken: cancellation);
            return new EdgeLeaderboardPage(
                reply.Entries.Select(entry => new EdgeLeaderboardEntry
                { SubjectId = entry.SubjectId, Score = entry.Score }).ToList(),
                reply.ReadVersion,
                reply.NextCursor.Length > 0 ? reply.NextCursor : null,
                reply.NextCursor.Length > 0);
        }
        catch (RpcException error) when (error.StatusCode == StatusCode.FailedPrecondition)
        {
            throw new EdgeLeaderboardStaleException(error.Status.Detail);
        }
        catch (RpcException error) when (error.StatusCode == StatusCode.InvalidArgument)
        {
            throw new ArgumentException(error.Status.Detail);
        }
    }

    /// <summary>One subject's position (RANK-05): rank, score and the read version at one
    /// view. An absent subject answers null (found=false is an answer, not an error).</summary>
    public async Task<EdgeLeaderboardRank?> GetRankAsync(string tenantId, string gameId,
        EdgeLeaderboardQuery query, string subjectId, CancellationToken cancellation)
    {
        var client = new Leaderboard.LeaderboardClient(_channel.Value);
        try
        {
            var reply = await client.GetRankAsync(new GetRankRequest
            {
                TenantId = tenantId,
                GameId = gameId,
                Board = ReadKey(gameId, query),
                SubjectId = subjectId,
            }, cancellationToken: cancellation);
            if (!reply.Found) return null;
            return new EdgeLeaderboardRank(reply.Rank, reply.Entry.SubjectId, reply.Entry.Score,
                reply.ReadVersion);
        }
        catch (RpcException error) when (error.StatusCode == StatusCode.InvalidArgument)
        {
            throw new ArgumentException(error.Status.Detail);
        }
    }

    /// <summary>The entries within a bounded window of a subject's rank (RANK-05). An
    /// absent subject answers null.</summary>
    public async Task<EdgeLeaderboardAround?> GetAroundAsync(string tenantId, string gameId,
        EdgeLeaderboardQuery query, string subjectId, CancellationToken cancellation)
    {
        var client = new Leaderboard.LeaderboardClient(_channel.Value);
        try
        {
            var reply = await client.GetAroundMeAsync(new GetAroundMeRequest
            {
                TenantId = tenantId,
                GameId = gameId,
                Board = ReadKey(gameId, query),
                SubjectId = subjectId,
                Window = query.Window,
            }, cancellationToken: cancellation);
            if (!reply.Found) return null;
            return new EdgeLeaderboardAround(
                reply.Entries.Select(entry => new EdgeLeaderboardEntry
                { Rank = entry.Rank, SubjectId = entry.Entry.SubjectId, Score = entry.Entry.Score }
                ).ToList(),
                reply.ReadVersion);
        }
        catch (RpcException error) when (error.StatusCode == StatusCode.InvalidArgument)
        {
            throw new ArgumentException(error.Status.Detail);
        }
    }

    /// <summary>The read key as the storage keys on it: board/period/scope/subject identity
    /// with neutral write-side descriptors (player-summed, default bounds, the all-time
    /// calendar). The same synthesis the back office reads through — reads never consult
    /// the descriptors, so a wrong descriptor value would answer an empty board rather than
    /// another board's rows; the subject type is the row filter.</summary>
    private static BoardDefinitionProto ReadKey(string gameId, EdgeLeaderboardQuery query)
    {
        if (query.Subject is < 0 or > 2)
            throw new ArgumentException("The subject type is not a RANK-01 subject type.");
        var periodId = query.PeriodId.Length > 0 ? query.PeriodId : "alltime";
        var scopeId = query.ScopeId.Length > 0 ? query.ScopeId : "global";
        return new BoardDefinitionProto
        {
            BoardId = query.Board,
            Subject = query.Subject,
            Aggregation = (int)RankAggregation.Sum,
            PeriodKind = (int)RankPeriodKind.AllTime,
            PeriodId = periodId,
            ScopeId = scopeId,
        };
    }
}

/// <summary>One page of a TopN walk: the ordered entries, the board's read version at the
/// same snapshot, and the next page's cursor exactly when more entries remain.</summary>
public sealed record EdgeLeaderboardPage(IReadOnlyList<EdgeLeaderboardEntry> Entries,
    string ReadVersion, string? NextCursor, bool HasMore);

/// <summary>One subject's position: its unique ordinal rank, its subject id and score, and
/// the board's read version at the same snapshot.</summary>
public sealed record EdgeLeaderboardRank(long Rank, string SubjectId, long Score, string ReadVersion);

/// <summary>The entries around one subject's rank, in rank order, at one read view.</summary>
public sealed record EdgeLeaderboardAround(IReadOnlyList<EdgeLeaderboardEntry> Entries,
    string ReadVersion);

/// <summary>A continuation whose board changed or whose snapshot lifetime expired. The
/// caller restarts the walk from the first page — the protocol never stitches pages
/// across versions (RANK-06).</summary>
public sealed class EdgeLeaderboardStaleException(string message) : Exception(message);

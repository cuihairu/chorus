using System.Globalization;
using Chorus.Data;
using Chorus.Rpc;
using Grpc.Core;
using Npgsql;

namespace Chorus.DataServer;

/// <summary>
/// FIX-RANK-03: the leaderboard scoring face of the data plane (developer-capabilities.md
/// 「Process ownership」— DataServer executes explicit idempotent reads and writes). The
/// business module (the ControlServer scoring service) validates the source and selects the
/// board/period/scope; this service stores. The apply re-verifies, in its transaction, that
/// the effect is a committed score effect whose own delta matches the request, so no score
/// value the data plane did not itself read from committed evidence can reach a board.
/// </summary>
public sealed class LeaderboardGrpcService(
    RedesignLeaderboardStore leaderboardStore, TrustedProcessCertificates trustedCertificates)
    : Leaderboard.LeaderboardBase
{
    public override Task<ReadyReply> Ready(Empty request, ServerCallContext context)
    {
        Require(context, "control-server", "game-server");
        return Task.FromResult(new ReadyReply { Ready = true, SchemaVersion = "1" });
    }

    /// <summary>The source read: the committed effect ledger answers one EffectId's kind and
    /// payload verbatim. Found with kind 'score' is the only shape the business module may
    /// project; a client self-report names an id that never appears here.</summary>
    public override async Task<CommittedScoreEffectReply> ReadCommittedScoreEffect(
        ReadCommittedScoreEffectRequest request, ServerCallContext context)
    {
        Require(context, "control-server", "game-server");
        try
        {
            var effect = await leaderboardStore.ReadCommittedEffectAsync(
                request.EffectId, context.CancellationToken);
            return effect is null
                ? new CommittedScoreEffectReply { Found = false }
                : new CommittedScoreEffectReply
                {
                    Found = true, Kind = effect.Kind, PayloadJson = effect.PayloadJson
                };
        }
        catch (ArgumentException error)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, error.Message));
        }
    }

    /// <summary>The idempotent apply: the business module passes the board it selected and
    /// the subject/delta it derived from the committed payload; the data plane re-verifies
    /// the committed source (kind 'score', matching delta) in the same transaction and then
    /// applies the effect exactly once per immutable EffectId. A refused source, a delta
    /// mismatch or an EffectId already applied to a different projection is a visible
    /// rejection; a replay or lost-response retry answers the stored score as a duplicate.
    /// </summary>
    public override async Task<ApplyScoreEffectReply> ApplyScoreEffect(
        ApplyScoreEffectRequest request, ServerCallContext context)
    {
        var caller = Require(context, "control-server", "game-server");
        var tenantId = ResolveTenant(caller, request.TenantId);
        try
        {
            var board = ToBoard(tenantId, request.GameId, request.Board);
            await using var connection = new NpgsqlConnection(leaderboardStore.ConnectionString);
            await connection.OpenAsync(context.CancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(context.CancellationToken);
            var result = await leaderboardStore.ApplyScoreEffectAsync(board, request.SubjectId,
                request.EffectId, request.Delta, connection, transaction, context.CancellationToken);
            await transaction.CommitAsync(context.CancellationToken);
            return new ApplyScoreEffectReply { Score = result.Score, Duplicate = result.Duplicate };
        }
        catch (LeaderboardRejectedException error)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, error.Message));
        }
        catch (IdempotencyConflictException)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument,
                "The effect id was already applied to a different projection."));
        }
        catch (ArgumentException error)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, error.Message));
        }
    }

    /// <summary>The bounded TopN of a board in its declared order, with the board's read
    /// version at the same snapshot (FIX-RANK-04). The query authorizes by role and tenant;
    /// a caller that bound its page to an expected version gets a stale read when the board
    /// changed since — the answer is never a cross-version page. An empty board answers an
    /// empty page and an empty version.</summary>
    public override async Task<TopNReply> GetTop(GetTopRequest request, ServerCallContext context)
    {
        var caller = Require(context, "control-server", "game-server", "streamer-server");
        var tenantId = ResolveTenant(caller, request.TenantId);
        try
        {
            var board = ToBoard(tenantId, request.GameId, request.Board);
            var result = await leaderboardStore.ReadTopWithVersionAsync(board, request.MaxEntries,
                context.CancellationToken);
            var version = VersionString(result.ReadVersion);
            if (request.ExpectedVersion.Length > 0
                && !string.Equals(request.ExpectedVersion, version, StringComparison.Ordinal))
                throw new LeaderboardStaleReadException(
                    "The board changed since the caller's read version; restart the read.");
            var reply = new TopNReply { BoardId = board.BoardId, ReadVersion = version };
            reply.Entries.Add(result.Entries.Select(entry => new RankEntryProto
            {
                Subject = (int)entry.Subject, SubjectId = entry.SubjectId, Score = entry.Score
            }));
            return reply;
        }
        catch (LeaderboardStaleReadException error)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, error.Message));
        }
        catch (ArgumentException error)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, error.Message));
        }
    }

    private static string VersionString(DateTimeOffset version) =>
        version == DateTimeOffset.MinValue ? "" : version.ToString("O", CultureInfo.InvariantCulture);

    private static LeaderboardBoard ToBoard(string tenantId, string gameId, BoardDefinitionProto proto)
    {
        if (proto is null)
            throw new ArgumentException("The request must carry the board definition.");
        var bounds = new ScoreBounds(proto.BoundsMin, proto.BoundsMax, proto.BoundsAllowNegative);
        var period = new RankPeriod((RankPeriodKind)proto.PeriodKind, proto.PeriodId,
            proto.PeriodTimezone, ParseUtc(proto.PeriodStartUtc), ParseUtc(proto.PeriodEndUtc),
            proto.PeriodVersion);
        return new LeaderboardBoard(tenantId, gameId, proto.BoardId,
            (RankSubject)proto.Subject, (RankAggregation)proto.Aggregation, bounds, period,
            new RankScope(proto.ScopeId));
    }

    private static DateTimeOffset? ParseUtc(string value) =>
        value.Length == 0
            ? null
            : DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

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

    private static string ResolveTenant(ProcessIdentity caller, string requested)
    {
        if (caller.TenantId == "@cluster")
        {
            if (string.IsNullOrWhiteSpace(requested))
                throw new RpcException(new Status(StatusCode.InvalidArgument,
                    "A cluster caller must name the tenant it acts for."));
            return requested;
        }
        if (requested.Length > 0 && !string.Equals(requested, caller.TenantId, StringComparison.Ordinal))
            throw new RpcException(new Status(StatusCode.PermissionDenied,
                "A tenant caller cannot act for another tenant."));
        return caller.TenantId;
    }
}

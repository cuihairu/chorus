using System.Globalization;
using System.Text.Json;
using Chorus.Data;
using Chorus.Rpc;
using Grpc.Core;

namespace Chorus.ControlServer;

/// <summary>
/// FIX-RANK-03: the declared mapping of the shipped example module family
/// (developer-capabilities.md 「A business leaderboard/settlement module within ControlServer
/// validates its source and selects board/period/scope」). A committed score effect carries
/// <c>{faction, delta, total}</c> and scores the faction onto a per-room Faction board —
/// Sum, default bounds, AllTime, scope = the effect's room (the effect id is
/// <c>{room}/{tick}/{index}</c>). The business layer owns this mapping: a caller never
/// chooses a board, a subject or an amount — it names an effect, and the policy reads the
/// subject and delta from the committed payload.
/// </summary>
public class LeaderboardScoringPolicy
{
    /// <summary>The board id of the example family's faction score board (the game declares
    /// it; the business layer resolves it, never the caller).</summary>
    public virtual string BoardId => "faction-score";

    public virtual RankSubject Subject => RankSubject.Faction;

    public virtual RankAggregation Aggregation => RankAggregation.Sum;

    public virtual ScoreBounds Bounds => ScoreBounds.Default;

    /// <summary>Reads the scoring subject id and its signed delta from a committed score
    /// payload (the declared shape: <c>{faction, delta, ...}</c>). A payload without the
    /// declared fields is not a score this family projects — the caller cannot project it.</summary>
    public virtual bool TryReadScore(JsonElement payload, out string subjectId, out long delta)
    {
        subjectId = "";
        delta = 0;
        if (payload.ValueKind != JsonValueKind.Object) return false;
        var faction = payload.TryGetProperty("faction", out var f) && f.ValueKind == JsonValueKind.String
            ? f.GetString()
            : null;
        var amount = payload.TryGetProperty("delta", out var d) && d.ValueKind == JsonValueKind.Number
            ? d.GetInt64()
            : (long?)null;
        if (string.IsNullOrWhiteSpace(faction) || amount is null) return false;
        subjectId = faction;
        delta = amount.Value;
        return true;
    }

    /// <summary>Resolves the board for one committed score effect: the subject, aggregation,
    /// bounds and board id are the declared mapping; the scope is the effect's room (the
    /// effect id's first segment). A period-less AllTime board is the default — period
    /// attribution is FIX-RANK-07. An effect id without a room segment is refused.</summary>
    public virtual LeaderboardBoard ResolveBoard(string tenantId, string gameId, string effectId)
    {
        var firstSlash = effectId.IndexOf('/');
        var roomId = firstSlash > 0 ? effectId[..firstSlash] : "";
        if (roomId.Length == 0)
            throw new LeaderboardRejectedException(
                "The effect id does not carry its room segment; the board scope cannot be resolved.");
        return new LeaderboardBoard(tenantId, gameId, BoardId, Subject, Aggregation, Bounds,
            RankPeriod.AllTime, new RankScope(roomId));
    }
}

/// <summary>
/// FIX-RANK-03: the data-plane face of the scoring business module. The production
/// implementation rides the <see cref="Leaderboard.LeaderboardClient"/> (the data host's
/// gRPC face); tests fake it, so the module's source validation and board selection are
/// unit-testable without a cluster.
/// </summary>
public interface ILeaderboardScoringDataPlane
{
    Task<CommittedScoreEffectReply> ReadCommittedScoreEffectAsync(
        ReadCommittedScoreEffectRequest request, CancellationToken cancellationToken = default);

    Task<ApplyScoreEffectReply> ApplyScoreEffectAsync(
        ApplyScoreEffectRequest request, CancellationToken cancellationToken = default);
}

/// <summary>The production data-plane implementation: the data host's leaderboard face.</summary>
public sealed class GrpcLeaderboardScoringDataPlane(Leaderboard.LeaderboardClient client)
    : ILeaderboardScoringDataPlane
{
    public Task<CommittedScoreEffectReply> ReadCommittedScoreEffectAsync(
        ReadCommittedScoreEffectRequest request, CancellationToken cancellationToken = default) =>
        client.ReadCommittedScoreEffectAsync(request, cancellationToken: cancellationToken).ResponseAsync;

    public Task<ApplyScoreEffectReply> ApplyScoreEffectAsync(
        ApplyScoreEffectRequest request, CancellationToken cancellationToken = default) =>
        client.ApplyScoreEffectAsync(request, cancellationToken: cancellationToken).ResponseAsync;
}

/// <summary>
/// FIX-RANK-03: the ControlServer business module that projects committed scoring effects
/// onto their declared boards, idempotently (developer-capabilities.md 「Score changes derive
/// from immutable committed server effects, identified by EffectId」). The only entry is
/// <see cref="ProjectScoreEffectAsync"/>, and it takes an effect id — never a score value:
/// the module reads the committed effect (the source), refuses anything that is not a
/// committed <c>score</c>-kind effect (a client self-report or an unrelated asset change),
/// derives the subject and delta from the committed payload (the declared shape), resolves
/// the board (the declared mapping) and asks the data plane to store. The data plane
/// re-verifies the committed source in its transaction, so a concurrent, replayed or
/// lost-response retry of one effect never double-scores. This module selects; it never
/// invents a score a committed effect did not carry.
/// </summary>
public sealed class LeaderboardScoringService(ILeaderboardScoringDataPlane dataPlane,
    LeaderboardScoringPolicy? policy = null)
{
    /// <summary>The committed effect kind that carries a board score.</summary>
    public const string ScoreEffectKind = "score";

    private readonly LeaderboardScoringPolicy _policy = policy ?? new LeaderboardScoringPolicy();

    /// <summary>Projects one committed scoring effect onto its declared board. The caller
    /// names the tenant, game and effect id — nothing else: the source, the subject, the
    /// delta and the board all come from the committed evidence and the declared mapping.</summary>
    public async Task<LeaderboardEffectResult> ProjectScoreEffectAsync(
        string tenantId, string gameId, string effectId, CancellationToken cancellationToken = default)
    {
        // Source validation (the business layer's own check, before any board selection): the
        // effect must be a committed score-kind effect — a fabricated id, an unrelated effect
        // kind (an asset change, a like) or a score with no committed effect is refused here.
        var committed = await dataPlane.ReadCommittedScoreEffectAsync(new ReadCommittedScoreEffectRequest
        {
            TenantId = tenantId, GameId = gameId, EffectId = effectId
        }, cancellationToken);
        if (!committed.Found)
            throw new LeaderboardRejectedException(
                "The effect is not a committed server effect; a client self-report never projects.");
        if (committed.Kind != ScoreEffectKind)
            throw new LeaderboardRejectedException(
                $"The committed effect is a {committed.Kind} effect, not a score effect; it never enters a board.");

        // The subject and the delta come from the committed payload (the declared shape),
        // never from the caller — the caller cannot choose an amount.
        using var payload = JsonDocument.Parse(committed.PayloadJson);
        if (!_policy.TryReadScore(payload.RootElement, out var subjectId, out var delta))
            throw new LeaderboardRejectedException(
                "The committed score effect does not carry the declared scoring shape.");

        // The board is the business layer's declared mapping — never the caller's.
        var board = _policy.ResolveBoard(tenantId, gameId, effectId);

        try
        {
            var reply = await dataPlane.ApplyScoreEffectAsync(new ApplyScoreEffectRequest
            {
                TenantId = tenantId, GameId = gameId, EffectId = effectId,
                Board = ToProto(board), SubjectId = subjectId, Delta = delta
            }, cancellationToken);
            return new LeaderboardEffectResult(reply.Score, reply.Duplicate);
        }
        catch (RpcException error) when (error.StatusCode == StatusCode.FailedPrecondition)
        {
            throw new LeaderboardRejectedException(error.Status.Detail);
        }
        catch (RpcException error) when (error.StatusCode == StatusCode.InvalidArgument)
        {
            throw new IdempotencyConflictException();
        }
    }

    private static BoardDefinitionProto ToProto(LeaderboardBoard board) => new()
    {
        BoardId = board.BoardId,
        Subject = (int)board.Subject,
        Aggregation = (int)board.Aggregation,
        PeriodKind = (int)board.Period.Kind,
        PeriodId = board.Period.PeriodId,
        PeriodTimezone = board.Period.Timezone,
        PeriodStartUtc = board.Period.StartUtc?.ToString("O", CultureInfo.InvariantCulture) ?? "",
        PeriodEndUtc = board.Period.EndUtc?.ToString("O", CultureInfo.InvariantCulture) ?? "",
        PeriodVersion = board.Period.Version,
        ScopeId = board.Scope.ScopeId,
        BoundsMin = board.Bounds.Min,
        BoundsMax = board.Bounds.Max,
        BoundsAllowNegative = board.Bounds.AllowNegative
    };
}

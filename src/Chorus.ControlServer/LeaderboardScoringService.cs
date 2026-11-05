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

    /// <summary>The declared period shape of this family's board (FIX-RANK-07): the base
    /// family is <see cref="RankPeriodKind.AllTime"/> and unbounded; a periodic family
    /// overrides this with a <see cref="RankPeriodKind.Daily"/> or <see cref="RankPeriodKind.Weekly"/>
    /// bucket plus an explicit <see cref="PeriodTimezone"/>.</summary>
    public virtual RankPeriodKind PeriodKind => RankPeriodKind.AllTime;

    /// <summary>The timezone that computes the period bucket. Only meaningful for a periodic
    /// family; an AllTime family never buckets.</summary>
    public virtual string PeriodTimezone => "UTC";

    /// <summary>How late after a period closes a settlement may still be attributed to that
    /// period (FIX-RANK-07). <see cref="TimeSpan.Zero"/> is the strict default: an effect
    /// projected after its source period closed is refused, never moved to a later bucket.</summary>
    public virtual TimeSpan LateSettlementWindow => TimeSpan.Zero;

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

    /// <summary>Resolves the period a committed score effect belongs to (FIX-RANK-07,
    /// developer-capabilities.md 「周期」「迟到结算有明确的周期归属」). The period is always the
    /// bucket of the effect's immutable source time — never the projection or retry time,
    /// which would silently move a late effect to the wrong bucket. An AllTime family is
    /// unbounded and never late; a periodic family buckets by <see cref="PeriodKind"/> and
    /// <see cref="PeriodTimezone"/> and refuses an effect projected after its source period
    /// closed by more than <see cref="LateSettlementWindow"/> — it is attributed to its
    /// original bucket or refused, never re-attributed to the moment it was processed.</summary>
    public virtual RankPeriod ResolvePeriod(DateTimeOffset sourceTimeUtc, DateTimeOffset nowUtc)
    {
        var period = RankPeriod.Resolve(PeriodKind, PeriodTimezone, sourceTimeUtc);
        if (period.EndUtc is { } end && nowUtc > end + LateSettlementWindow)
            throw new LeaderboardRejectedException(
                "The board period closed for late settlement; the effect is too old to be attributed to its source period.");
        return period;
    }

    /// <summary>Resolves the board for one committed score effect: the subject, aggregation,
    /// bounds and board id are the declared mapping; the scope is the effect's room (the
    /// effect id's first segment); the period is the one <see cref="ResolvePeriod"/> attributed
    /// from the effect's source time. An effect id without a room segment is refused.</summary>
    public virtual LeaderboardBoard ResolveBoard(string tenantId, string gameId, string effectId,
        RankPeriod period)
    {
        var firstSlash = effectId.IndexOf('/');
        var roomId = firstSlash > 0 ? effectId[..firstSlash] : "";
        if (roomId.Length == 0)
            throw new LeaderboardRejectedException(
                "The effect id does not carry its room segment; the board scope cannot be resolved.");
        return new LeaderboardBoard(tenantId, gameId, BoardId, Subject, Aggregation, Bounds,
            period, new RankScope(roomId));
    }
}

/// <summary>
/// FIX-RANK-07: the example family's daily period variant (developer-capabilities.md
/// 「周期」). The same faction score effect is attributed to the daily bucket (UTC) of its
/// committed source time and lands on a distinct board id, so one day's scores never bleed
/// into another day's board. A late effect (a settlement projected after its source day
/// closed) is attributed to that original day's bucket while it arrives within the declared
/// 48-hour late-settlement window; beyond the window it is refused, never re-attributed to
/// the day it was processed.
/// </summary>
public class LeaderboardDailyFactionScoringPolicy : LeaderboardScoringPolicy
{
    public override string BoardId => "faction-score-daily";

    public override RankPeriodKind PeriodKind => RankPeriodKind.Daily;

    public override string PeriodTimezone => "UTC";

    public override TimeSpan LateSettlementWindow => TimeSpan.FromHours(48);
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

        // The period is attributed from the effect's immutable source time (FIX-RANK-07) —
        // never from the projection or retry time. A score effect with no source time cannot
        // be attributed to a period and is refused; a late effect is attributed to its source
        // bucket or refused by the policy's declared window, never moved to the moment it was
        // processed.
        if (string.IsNullOrWhiteSpace(committed.CommittedAt))
            throw new LeaderboardRejectedException(
                "The committed score effect carries no source time; it cannot be attributed to a period.");
        var sourceTimeUtc = DateTimeOffset.Parse(committed.CommittedAt,
            CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        var period = _policy.ResolvePeriod(sourceTimeUtc, DateTimeOffset.UtcNow);

        // The board is the business layer's declared mapping — never the caller's.
        var board = _policy.ResolveBoard(tenantId, gameId, effectId, period);

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

    /// <summary>The RANK-01 board definition as the data-plane proto (FIX-RANK-03 shape,
    /// reused by the query module of FIX-RANK-04): both business modules name the board the
    /// same way, so a projected board and a queried board can never drift apart.</summary>
    internal static BoardDefinitionProto ToProto(LeaderboardBoard board) => new()
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

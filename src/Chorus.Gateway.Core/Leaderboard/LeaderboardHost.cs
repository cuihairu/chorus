using Chorus.Data;
using Chorus.Engine.Abstractions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Chorus.Gateway.Core.Leaderboard;

/// <summary>
/// Host-side score sink (docs/guide/leaderboard.md): projects the authoritative
/// session scores pushed by game modules into the room board of this host's
/// <see cref="LeaderboardStore"/> - scope <see cref="LeaderboardScope.Room"/>,
/// scope id = session id. Room boards are replace-projections by design
/// (audit §9.2): re-projecting the same participant is idempotent.
/// </summary>
public sealed class LeaderboardScoreSink : ISessionScoreSink
{
    private readonly LeaderboardStore _store;

    public LeaderboardScoreSink(LeaderboardStore store) => _store = store;

    public void ProjectScore(string sessionId, string participantId, long score) =>
        _store.UpsertScore(LeaderboardScope.Room, sessionId, participantId, score);
}

/// <summary>
/// Read-only leaderboard query surface (docs/guide/leaderboard.md). Hosted by
/// the session-owning hosts (Chorus.Server / Chorus.EngineHost): the gateway
/// holds no session state and exposes nothing. Scope is the path name
/// (<c>room</c>/<c>match</c>/<c>period</c>/<c>global</c>), scope id is the board
/// key (session id for room boards, period label such as <c>day:2026-10-14</c>
/// for period boards). Boards that were never written return an empty entries
/// list - a missing board is not an error.
/// </summary>
public static class LeaderboardHttp
{
    /// <summary>Top-N ceiling for a single query (docs/reference/api.md).</summary>
    public const int MaxTop = 1000;

    private static readonly IReadOnlyDictionary<string, LeaderboardScope> Scopes = new Dictionary<string, LeaderboardScope>(StringComparer.Ordinal)
    {
        ["room"] = LeaderboardScope.Room,
        ["match"] = LeaderboardScope.Match,
        ["period"] = LeaderboardScope.Period,
        ["global"] = LeaderboardScope.Global,
    };

    public static void MapLeaderboard(this IEndpointRouteBuilder app)
    {
        app.MapGet("/leaderboard/{scope}/{scopeId}", HandleAsync);
    }

    private static IResult HandleAsync(string scope, string scopeId, int? top, LeaderboardStore store)
    {
        if (!Scopes.TryGetValue(scope?.ToLowerInvariant() ?? string.Empty, out var parsed))
        {
            return Results.Json(new { error = "unknown_scope", scope }, statusCode: StatusCodes.Status400BadRequest);
        }

        // Single path segment by route shape: a "/" in the scope id cannot be
        // expressed here (callers needing one POST it to the aggregation side
        // instead - see docs/guide/leaderboard.md 边界).
        if (string.IsNullOrWhiteSpace(scopeId))
        {
            return Results.Json(new { error = "invalid_scope_id" }, statusCode: StatusCodes.Status400BadRequest);
        }

        if (top is < 1 or > MaxTop)
        {
            return Results.Json(new { error = "invalid_top", top, max = MaxTop }, statusCode: StatusCodes.Status400BadRequest);
        }

        return Results.Json(new
        {
            scope = parsed.ToString().ToLowerInvariant(),
            scopeId,
            frozen = store.IsFrozen(parsed, scopeId),
            entries = store.Query(parsed, scopeId, top)
        });
    }
}

using System.Text.Json;

namespace Chorus.ExampleGame;

/// <summary>
/// FIX-SYNC-08: the shared frozen-state terminal result (authoritative-sync.md:54). The
/// pause-timeout ending derives the round result from the frozen <see cref="ExampleGameState"/>
/// — the same canonical ranking/winner document a normal final tick produces, but computed
/// from the scores the room froze at rather than a new tick's deltas. The state is untouched:
/// no new frame, no hash change, no tick advance. Modules with extra result fields (V3's
/// likes) pass them through.
/// </summary>
public static class ModuleResult
{
    private static readonly JsonSerializerOptions Canonical = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    /// <summary>Builds the terminal result document from the frozen scores, or null when the
    /// state carries no score map (a malformed frozen state — the caller refuses rather than
    /// invent a ranking).</summary>
    public static string? BuildResult(ExampleGameState state,
        IReadOnlyDictionary<string, long>? likes = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.ScoreByFaction is null || state.ScoreByFaction.Count == 0)
            return null;
        var ranking = state.ScoreByFaction
            .Select(entry => new { faction = entry.Key, score = entry.Value })
            .OrderByDescending(entry => entry.score, Comparer<long>.Default)
            .ThenBy(entry => entry.faction, StringComparer.Ordinal).ToArray();
        if (likes is null)
        {
            return JsonSerializer.Serialize(new
            {
                ranking, winner = ranking[0].score == ranking[^1].score
                    ? "draw" : ranking[0].faction
            }, Canonical);
        }
        var applied = likes
            .Select(entry => new { faction = entry.Key, count = entry.Value })
            .OrderBy(entry => entry.faction, StringComparer.Ordinal).ToArray();
        return JsonSerializer.Serialize(new
        {
            ranking, winner = ranking[0].score == ranking[^1].score
                ? "draw" : ranking[0].faction,
            likes = applied
        }, Canonical);
    }
}

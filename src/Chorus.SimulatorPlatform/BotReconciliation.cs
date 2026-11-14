using System.Text.Json;

namespace Chorus.SimulatorPlatform;

/// <summary>What the data plane says one room received: delivery rows (all states and
/// the consumed subset) grouped by the stored event kind, the frozen result's faction
/// scores, and the round-score settlement grants per player.</summary>
public sealed record RoomLedger(
    long Delivered, long Consumed,
    IReadOnlyDictionary<ScriptEventKind, long> DeliveredByKind,
    long UnknownKind,
    IReadOnlyDictionary<string, long> ResultScores,
    IReadOnlyDictionary<string, long> GrantDeltas);

/// <summary>What the fleet swore it would do, derived before the run from the scripts
/// and the mounted streamers: per-kind sends, per-faction gift value (the demo module's
/// score), and per-player settlement deltas (each participant's faction score).</summary>
public sealed record RunExpectation(
    IReadOnlyDictionary<ScriptEventKind, long> SentByKind,
    IReadOnlyDictionary<string, long> ScoresByFaction,
    IReadOnlyDictionary<string, long> DeltasByPlayer);

/// <summary>The run held to its word: the deviations list is empty exactly when every
/// sent event reached a committed frame and every expected score and grant landed.</summary>
public sealed record ReconciliationReport(IReadOnlyList<string> Deviations)
{
    public bool IsClean => Deviations.Count == 0;
}

/// <summary>
/// The BOTS-1 assertion library: a run is reconciled against the data plane itself —
/// not the poster's word, not the room's stream. Sent N must mean delivered N per kind
/// (the gateway's dedup keys the ids, so a shared sequence makes the counts speak),
/// consumed must equal delivered (nothing stuck pending in the durable inbox), and the
/// faction scores and settlement deltas must equal the pulse-derived sums exactly.
/// </summary>
public static class BotReconciliation
{
    /// <summary>The demo module's settlement grant key for a round's faction scores.</summary>
    public const string RoundScoreGrantKey = "round-score";

    /// <summary>Derives the run's expectations from the mounted streamers and their
    /// scripts. Gifts are unit-count events scored by value, so a faction's expected
    /// score (and its participants' grant delta) is exactly its scripts' gift value sum.</summary>
    public static RunExpectation Expect(
        IReadOnlyList<(BotStreamerHandle Streamer, string Faction, BotScript Script)> runs)
    {
        var byKind = new Dictionary<ScriptEventKind, long>();
        var scores = new Dictionary<string, long>();
        var deltas = new Dictionary<string, long>();
        foreach (var (streamer, faction, script) in runs)
        {
            foreach (var (kind, count) in script.Totals.ByKind)
                byKind[kind] = byKind.GetValueOrDefault(kind) + count;
            scores[faction] = scores.GetValueOrDefault(faction) + script.Totals.GiftValueSum;
            deltas[streamer.StreamerId] = deltas.GetValueOrDefault(streamer.StreamerId)
                + script.Totals.GiftValueSum;
        }
        return new RunExpectation(byKind, scores, deltas);
    }

    /// <summary>The delivery-level core both reconciliations share: every post accepted
    /// at the face, sent N delivered N per kind, consumed equal to delivered (nothing
    /// stuck pending in the durable inbox), and no deliveries carrying an unknown kind.</summary>
    public static IReadOnlyList<string> DeliveryDeviations(
        IReadOnlyDictionary<ScriptEventKind, long> expectedSent, RoomLedger ledger,
        IReadOnlyList<PosterSnapshot> posters)
    {
        var deviations = new List<string>();

        var posted = posters.Sum(p => p.SentByKind.Values.Sum());
        var accepted = posters.Sum(p => p.Accepted);
        if (accepted != posted)
            deviations.Add(
                $"face: accepted {accepted} of {posted} posts (duplicated {posters.Sum(p => p.Duplicated)}," +
                $" unauthorized {posters.Sum(p => p.Unauthorized)}," +
                $" rate-limited {posters.Sum(p => p.RateLimited)}, other {posters.Sum(p => p.Other)})");

        foreach (var kind in expectedSent.Keys.Union(ledger.DeliveredByKind.Keys))
        {
            var sent = expectedSent.GetValueOrDefault(kind);
            var delivered = ledger.DeliveredByKind.GetValueOrDefault(kind);
            if (sent != delivered)
                deviations.Add($"{kind}: sent {sent} delivered {delivered}");
        }
        if (ledger.UnknownKind > 0)
            deviations.Add($"ledger: {ledger.UnknownKind} deliveries carry an unknown event kind");
        if (ledger.Consumed != ledger.Delivered)
            deviations.Add($"delivery: {ledger.Delivered - ledger.Consumed} of {ledger.Delivered}" +
                " never reached a committed frame");
        return deviations;
    }

    /// <summary>Holds the ledger and the posters' own word to the expectations. Every
    /// mismatch names itself; an empty list is the pass.</summary>
    public static ReconciliationReport Deviations(RunExpectation expected, RoomLedger ledger,
        IReadOnlyList<PosterSnapshot> posters)
    {
        var deviations = DeliveryDeviations(expected.SentByKind, ledger, posters).ToList();

        foreach (var (faction, score) in expected.ScoresByFaction)
            if (!ledger.ResultScores.TryGetValue(faction, out var actual) || actual != score)
                deviations.Add($"score: faction {faction} expected {score}" +
                    $" got {ledger.ResultScores.GetValueOrDefault(faction)}");
        foreach (var (faction, score) in ledger.ResultScores)
            if (!expected.ScoresByFaction.ContainsKey(faction) && score != 0)
                deviations.Add($"score: faction {faction} scored {score} with no expected gifts");

        foreach (var (player, delta) in expected.DeltasByPlayer)
            if (!ledger.GrantDeltas.TryGetValue(player, out var actual) || actual != delta)
                deviations.Add($"grant: player {player} expected {delta}" +
                    $" got {ledger.GrantDeltas.GetValueOrDefault(player)}");

        return new ReconciliationReport(deviations);
    }

    /// <summary>Reads one room's ledger straight from the data plane store: the durable
    /// inbox's delivery rows for the room's consumer scope, the frozen result, and the
    /// settlement grants. Runs before any deviation check — callers may poll it.</summary>
    public static async Task<RoomLedger> ReadRoomAsync(string connectionString, string roomId)
    {
        await using var connection = new Npgsql.NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        var scope = "room:" + roomId;

        var byKind = new Dictionary<ScriptEventKind, long>();
        long delivered = 0, consumed = 0, unknown = 0;
        await using (var command = new Npgsql.NpgsqlCommand("""
            SELECT e.payload->>'kind' AS kind, d.state, count(*) AS n
            FROM input.delivery d
            JOIN input.event e ON e.event_id = d.event_id
            WHERE d.consumer_scope = @scope
            GROUP BY 1, 2
            """, connection))
        {
            command.Parameters.AddWithValue("scope", scope);
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var count = reader.GetInt64(2);
                delivered += count;
                if (reader.GetString(1) == "Consumed") consumed += count;
                var stored = reader.IsDBNull(0) ? null : reader.GetString(0);
                if (stored is null || !long.TryParse(stored, out var code)
                    || !Enum.IsDefined((ScriptEventKind)code))
                {
                    unknown += count;
                    continue;
                }
                var kind = (ScriptEventKind)code;
                byKind[kind] = byKind.GetValueOrDefault(kind) + count;
            }
        }

        var scores = new Dictionary<string, long>();
        await using (var command = new Npgsql.NpgsqlCommand(
            "SELECT result::text FROM game.result WHERE room_id=@room " +
            "ORDER BY result_version DESC LIMIT 1", connection))
        {
            command.Parameters.AddWithValue("room", roomId);
            if (await command.ExecuteScalarAsync() is string result)
            {
                using var document = JsonDocument.Parse(result);
                foreach (var entry in document.RootElement.GetProperty("ranking").EnumerateArray())
                    scores[entry.GetProperty("faction").GetString()!] =
                        entry.GetProperty("score").GetInt64();
            }
        }

        var deltas = new Dictionary<string, long>();
        await using (var command = new Npgsql.NpgsqlCommand(
            "SELECT player_id, delta FROM settlement.grant WHERE room_id=@room AND grant_key=@key",
            connection))
        {
            command.Parameters.AddWithValue("room", roomId);
            command.Parameters.AddWithValue("key", RoundScoreGrantKey);
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var player = reader.GetString(0);
                deltas[player] = deltas.GetValueOrDefault(player) + reader.GetInt64(1);
            }
        }

        return new RoomLedger(delivered, consumed, byKind, unknown, scores, deltas);
    }
}

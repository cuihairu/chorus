using Npgsql;

namespace Chorus.ControlServer;

/// <summary>
/// Reads the round facts a platform status report carries (FIX-U3D-14): the committed result
/// json (its ranking and winner — the same frozen evidence the end ladder's begin recorded)
/// and each mount's platform room key (the launch code the platform bound this live session
/// with — kuaishou's roomCode). The end ladder's platform tasks report outward from these
/// facts; a read that fails is the data plane's outage, the enqueue's own retry cadence.
/// </summary>
public sealed class RoundFulfillmentReader(string connectionString)
{
    /// <summary>The room's committed result, and the room code per binding id. The result is
    /// read as the room-scoped fact it is — one frozen verdict per room (the frame store
    /// commits it under version 1, while the begin's recorded version is the room's own state
    /// counter, so the operation's version never keys this row). A missing row throws — the
    /// ladder only begins on a frozen result, so an absent row is a data-plane fault the
    /// enqueue retries, never an empty report.</summary>
    public async Task<RoundFulfillmentFacts> ReadAsync(string roomId,
        IReadOnlyList<EndMountGeneration> bindings, CancellationToken cancellationToken = default)
    {
        var roomCodes = new Dictionary<string, string>(StringComparer.Ordinal);
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var result = new NpgsqlCommand(
            "SELECT result::text FROM game.result WHERE room_id=@room " +
            "ORDER BY result_version DESC LIMIT 1", connection);
        result.Parameters.AddWithValue("room", roomId);
        var resultJson = await result.ExecuteScalarAsync(cancellationToken) as string
            ?? throw new InvalidOperationException(
                $"The round result {roomId} is not committed; the end ladder " +
                "begins on a frozen result, so this is a data-plane fault.");

        foreach (var binding in bindings)
        {
            await using var code = new NpgsqlCommand(
                """
                SELECT s.platform_room_key
                FROM live.binding b
                JOIN live.session s ON s.live_session_id = b.live_session_id
                WHERE b.binding_id = @binding
                """, connection);
            code.Parameters.AddWithValue("binding", binding.BindingId);
            roomCodes[binding.BindingId] =
                (await code.ExecuteScalarAsync(cancellationToken) as string) ?? "";
        }
        return new RoundFulfillmentFacts(resultJson, roomCodes);
    }
}

/// <summary>The round's committed result and the platform room key per binding id.</summary>
public sealed record RoundFulfillmentFacts(string ResultJson,
    IReadOnlyDictionary<string, string> RoomCodes);

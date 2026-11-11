using System.Text;
using System.Text.Json;

namespace Chorus.Platform;

/// <summary>
/// The kuaishou fulfillment contract (FIX-U3D-14), from docs/reference/platform-unity-sdks.md
/// §2.2 — the launch-required server duties the platform gates review on: the bind lifecycle
/// and the round status report. This adapter translates one platform task's desired json into
/// the documented HTTP shapes: the end ladder's roundEnd category rides
/// <c>POST /openapi/developer/live/smallPlay/round</c> (action stop, the round's result), with
/// app_id and access_token as query parameters (never in the body) and the sign over the sorted
/// core fields with app_id included and app_secret appended last. The categories the end ladder
/// declares not-required (gameplayUnbind, liveEnd) stay not-required here — the adapter
/// translates a declaration, it never invents a call. NotProductionReady until the real
/// endpoints are verified (the admission matrix's needs-permission rows): the documented
/// digest recipe is canonicalized here, but the hash algorithm itself is unverified until
/// joint debugging, so the digest lives in one swappable place.
/// </summary>
public sealed class KuaishouFulfillmentAdapter(
    Uri endpoint, string appId, string accessToken, string appSecret) : IPlatformTaskAdapter
{
    private static readonly HttpClient Client = new();

    public string Platform => "kuaishou";

    public bool ProductionReady => false;

    /// <summary>The documented kuaishou APIs answer synchronously but carry no per-operation
    /// record to query — the bind status answers whether a bind is open, never whether one
    /// stop call executed. An outcome the caller could not observe can therefore neither be
    /// re-issued nor resolved by query: it escalates to human attention, the task protocol's
    /// honest path for a platform that cannot answer for its own operations.</summary>
    public bool IdempotentReissue => false;

    public async Task<PlatformTaskOutcome> CallAsync(PlatformTaskCall call,
        CancellationToken cancellationToken = default)
    {
        using var desired = JsonDocument.Parse(call.DesiredJson);
        var root = desired.RootElement;
        if (!DeclaredRoundEndApply(root))
            return new PlatformTaskOutcome(false,
                """{"basis":"the end ladder declared no platform round change for this operation"}""",
                NotRequired: true);

        // The stop report's content comes from the desired — the round's committed result
        // (its ranking and winner) and the room code the launch carried. A desired without
        // them is a wiring fault, refused visibly so the task retries, never a silent skip.
        var roomCode = root.TryGetProperty("roomCode", out var code) && code.GetString() is { Length: > 0 }
            ? code.GetString()! : "";
        var resultJson = root.TryGetProperty("roundResult", out var result) && result.GetString() is { Length: > 0 }
            ? result.GetString()! : "";
        if (roomCode.Length == 0 || resultJson.Length == 0)
            throw new InvalidOperationException(
                "The end-round desired carries no room code or no committed round result; " +
                "the stop report cannot be built.");

        using var resultDocument = JsonDocument.Parse(resultJson);
        var winner = resultDocument.RootElement.TryGetProperty("winner", out var won)
            ? won.GetString() ?? "" : "";
        var fields = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["module"] = "round",
            ["action"] = "stop",
            ["roomCode"] = roomCode,
            ["roundId"] = root.TryGetProperty("roomId", out var room) ? room.GetString() ?? "" : "",
            ["roundType"] = "singleGroup", // the declared mapping: one streamer, faction rounds
            ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                .ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["data"] = JsonSerializer.Serialize(new
            {
                roomId = root.TryGetProperty("roomId", out var roomId) ? roomId.GetString() ?? "" : "",
                endOperationId = root.TryGetProperty("endOperationId", out var endOp)
                    ? endOp.GetString() ?? "" : "",
                resultVersion = root.TryGetProperty("resultVersion", out var version)
                    ? version.GetInt64() : 0,
                winner,
            }),
        };
        fields["sign"] = KuaishouContract.Sign(fields, appId, appSecret);

        // The documented rate limit (10 calls/s per app_id) rides the task protocol's own
        // retry cadence — a refused call parks retryable, it is never hammered.
        using var content = new FormUrlEncodedContent(fields);
        var response = await Client.PostAsync(new Uri(endpoint,
            "openapi/developer/live/smallPlay/round?app_id=" + Uri.EscapeDataString(appId) +
            "&access_token=" + Uri.EscapeDataString(accessToken)), content, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        // The documented success word: HTTP 200 and result=1; anything else is a platform
        // answer of "not executed" — a successful exchange the task protocol retries.
        var executed = response.IsSuccessStatusCode && body.Contains("\"result\":1", StringComparison.Ordinal);
        return new PlatformTaskOutcome(executed, body);
    }

    /// <summary>Kuaishou documents no per-operation record to query (see
    /// <see cref="IdempotentReissue"/>): the query leg answers nothing.</summary>
    public Task<PlatformTaskOutcome> QueryAsync(string operationId,
        CancellationToken cancellationToken = default) =>
        throw new HttpRequestException(
            "kuaishou documents no per-operation record; an unobserved outcome escalates.");

    /// <summary>Whether the desired declares a platform round change: the end ladder's own
    /// category word, read — never guessed — from the operation's declaration.</summary>
    private static bool DeclaredRoundEndApply(JsonElement root) =>
        root.TryGetProperty("categories", out var categories)
        && categories.TryGetProperty("roundEnd", out var roundEnd)
        && roundEnd.GetString() == "apply";
}

/// <summary>The documented sign recipe (docs/reference/platform-unity-sdks.md §2.2 「Bind」):
/// the core fields sorted, app_id included, app_secret appended last. The canonical string is
/// the documented part; the digest over it is the joint-debugging unknown, isolated here.</summary>
public static class KuaishouContract
{
    /// <summary>The canonical string a kuaishou sign commits to: field pairs sorted by key,
    /// app_id among them, the app secret appended raw after the last pair.</summary>
    public static string CanonicalPayload(IEnumerable<KeyValuePair<string, string>> fields,
        string appId, string appSecret)
    {
        var pairs = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["app_id"] = appId,
        };
        foreach (var (key, value) in fields)
            if (key != "sign")
                pairs[key] = value;
        var canonical = new StringBuilder();
        foreach (var (key, value) in pairs)
            canonical.Append(key).Append('=').Append(value).Append('&');
        return canonical.Append(appSecret).ToString();
    }

    /// <summary>The digest. SHA256 hex stands in for the unverified algorithm (the admission
    /// matrix's needs-permission row); joint debugging names the real one and only this
    /// method moves.</summary>
    public static string Sign(IEnumerable<KeyValuePair<string, string>> fields,
        string appId, string appSecret)
    {
        var payload = CanonicalPayload(fields, appId, appSecret);
        var digest = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexString(digest).ToLowerInvariant();
    }
}

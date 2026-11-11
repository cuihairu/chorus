using System.Text.Json;

namespace Chorus.Platform;

/// <summary>
/// The douyin fulfillment ACK (FIX-U3D-14), from docs/reference/platform-unity-sdks.md §1 —
/// the overview page's mandatory duty: the fulfillment ACK reporting API must be integrated,
/// and the platform will force review rejection without it. This adapter carries the duty as
/// the end ladder's roundEnd category: one report per round end, naming the round and its
/// committed result. The endpoint and auth shape themselves are the admission matrix's
/// needs-permission rows — the path below names the duty, the body carries the facts a
/// fulfillment report owes (the round, its result version, its winner), and nothing here is
/// invented beyond that. NotProductionReady until joint debugging verifies the real shape.
/// </summary>
public sealed class DouyinFulfillmentAdapter(Uri endpoint, string appId) : IPlatformTaskAdapter
{
    private static readonly System.Net.Http.HttpClient Client = new();

    public string Platform => "douyin";

    public bool ProductionReady => false;

    /// <summary>The ACK API's idempotency and query semantics are unverified (needs
    /// permission): an unobserved outcome escalates to human attention rather than riding
    /// on a re-issue nobody has proven safe.</summary>
    public bool IdempotentReissue => false;

    public async Task<PlatformTaskOutcome> CallAsync(PlatformTaskCall call,
        CancellationToken cancellationToken = default)
    {
        using var desired = JsonDocument.Parse(call.DesiredJson);
        var root = desired.RootElement;
        if (!root.TryGetProperty("categories", out var categories)
            || !categories.TryGetProperty("roundEnd", out var roundEnd)
            || roundEnd.GetString() != "apply")
            return new PlatformTaskOutcome(false,
                """{"basis":"the end ladder declared no platform round change for this operation"}""",
                NotRequired: true);

        var resultJson = root.TryGetProperty("roundResult", out var result) && result.GetString() is { Length: > 0 }
            ? result.GetString()! : "";
        if (resultJson.Length == 0)
            throw new InvalidOperationException(
                "The end-round desired carries no committed round result; the fulfillment " +
                "ack cannot be built.");

        using var resultDocument = JsonDocument.Parse(resultJson);
        var payload = JsonSerializer.Serialize(new
        {
            operationId = call.OperationId,
            roomId = root.TryGetProperty("roomId", out var room) ? room.GetString() ?? "" : "",
            resultVersion = root.TryGetProperty("resultVersion", out var version) ? version.GetInt64() : 0,
            winner = resultDocument.RootElement.TryGetProperty("winner", out var won)
                ? won.GetString() ?? "" : "",
        });
        using var content = new System.Net.Http.StringContent(payload, System.Text.Encoding.UTF8,
            "application/json");
        var response = await Client.PostAsync(new Uri(endpoint,
            "fulfillment/ack?app_id=" + Uri.EscapeDataString(appId)), content, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        return new PlatformTaskOutcome(response.IsSuccessStatusCode, body);
    }

    /// <summary>The ACK API documents no query face (needs permission): the query leg
    /// answers nothing, and the protocol escalates an unobserved outcome.</summary>
    public Task<PlatformTaskOutcome> QueryAsync(string operationId,
        CancellationToken cancellationToken = default) =>
        throw new HttpRequestException(
            "douyin documents no ack query face; an unobserved outcome escalates.");
}

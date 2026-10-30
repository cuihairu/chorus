using System.Text.Json;

namespace Chorus.Platform;

/// <summary>
/// Douyin mini-game launch adapter calling the officially verified code2Session OpenAPI
/// (docs/zh/reference/platform-douyin.md §2.2, verified 2026-10-14). The launch credential
/// carries the client-side login code: a bare value is a tt.login <c>code</c>; a value
/// prefixed with <c>anonymous:</c> is an <c>anonymous_code</c>. The canonical live session
/// key is derived deterministically from the verified identity — one canonical live session
/// per streamer per app — because no official live-room binding API is verified yet.
/// AppId/AppSecret are injected via configuration and never appear in errors or logs.
/// </summary>
public sealed class DouyinMiniGameLaunchAdapter : IPlatformLaunchAdapter
{
    public const string PlatformName = "douyin";
    public const string AnonymousCredentialPrefix = "anonymous:";

    public const string DefaultApiBaseUrl = "https://minigame.zijieapi.com";
    private const string Code2SessionPath = "/mgplatform/api/apps/jscode2session";

    private readonly string _appId;
    private readonly string _appSecret;
    private readonly HttpMessageInvoker _http;
    private readonly string _baseUrl;
    private readonly Func<DateTimeOffset> _clock;

    public DouyinMiniGameLaunchAdapter(string appId, string appSecret, HttpMessageInvoker http,
        string? apiBaseUrl = null, Func<DateTimeOffset>? clock = null)
    {
        if (string.IsNullOrWhiteSpace(appId))
            throw new InvalidOperationException("The douyin launch adapter requires an AppId.");
        if (string.IsNullOrWhiteSpace(appSecret))
            throw new InvalidOperationException(
                "The douyin launch adapter requires an AppSecret; refusing to start an adapter that cannot verify.");
        _appId = appId;
        _appSecret = appSecret;
        _http = http;
        _baseUrl = string.IsNullOrWhiteSpace(apiBaseUrl) ? DefaultApiBaseUrl : apiBaseUrl.TrimEnd('/');
        _clock = clock ?? (static () => DateTimeOffset.UtcNow);
    }

    /// <summary>Convenience constructor for hosts without a shared HTTP invoker.</summary>
    public DouyinMiniGameLaunchAdapter(string appId, string appSecret,
        string? apiBaseUrl = null, Func<DateTimeOffset>? clock = null)
        : this(appId, appSecret, new HttpClient(), apiBaseUrl, clock)
    {
    }

    public string Platform => PlatformName;

    public PlatformLaunchCapabilities Capabilities { get; } = new(
        PlatformName,
        ProductionReady: true,
        Evidence: "official code2session openapi verified 2026-10-14 (docs/zh/reference/platform-douyin.md §2.2)");

    public async Task<VerifiedLaunch> VerifyLaunchAsync(PlatformLaunchRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.AppId) ||
            string.IsNullOrWhiteSpace(request.LaunchCredential) ||
            string.IsNullOrWhiteSpace(request.LoginAttemptId))
            throw new PlatformLaunchRejectedException("malformed-credential",
                "AppId, launch credential, and login attempt id are required.");
        if (!StringComparer.Ordinal.Equals(request.AppId, _appId))
            throw new PlatformLaunchRejectedException("app-mismatch",
                "The launch credential was issued for another AppId.");

        string? code = null, anonymousCode = null;
        if (request.LaunchCredential.StartsWith(AnonymousCredentialPrefix, StringComparison.Ordinal))
        {
            anonymousCode = request.LaunchCredential[AnonymousCredentialPrefix.Length..];
            if (string.IsNullOrWhiteSpace(anonymousCode))
                throw new PlatformLaunchRejectedException("malformed-credential",
                    "The launch credential prefix requires an anonymous code.");
        }
        else
        {
            code = request.LaunchCredential;
        }

        var query = $"appid={Uri.EscapeDataString(_appId)}" +
                    $"&secret={Uri.EscapeDataString(_appSecret)}";
        if (code is not null) query += $"&code={Uri.EscapeDataString(code)}";
        if (anonymousCode is not null) query += $"&anonymous_code={Uri.EscapeDataString(anonymousCode)}";

        string json;
        try
        {
            using var httpRequest = new HttpRequestMessage(HttpMethod.Get, $"{_baseUrl}{Code2SessionPath}?{query}");
            using var response = await _http.SendAsync(httpRequest, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new PlatformLaunchRejectedException("platform-unreachable",
                    $"code2session returned HTTP {(int)response.StatusCode}.");
            json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            throw new PlatformLaunchRejectedException("platform-unreachable",
                "The douyin code2session endpoint could not be reached.");
        }

        JsonElement root;
        try
        {
            root = JsonDocument.Parse(json).RootElement;
        }
        catch (JsonException)
        {
            throw new PlatformLaunchRejectedException("platform-response-invalid",
                "The code2session response is not JSON.");
        }

        // Missing error is treated as failure (fail-closed, never guess success).
        var error = root.TryGetProperty("error", out var errorElement) && errorElement.ValueKind == JsonValueKind.Number
            ? errorElement.GetInt64()
            : -1L;
        if (error != 0)
        {
            var message = ReadString(root, "errmsg") ?? ReadString(root, "message") ?? "unknown douyin error";
            throw new PlatformLaunchRejectedException("platform-rejected",
                $"code2session rejected the credential (error {error}): {message}.");
        }

        var openid = ReadString(root, "openid");
        var anonymousOpenid = ReadString(root, "anonymous_openid");
        if (string.IsNullOrEmpty(openid) && string.IsNullOrEmpty(anonymousOpenid))
            throw new PlatformLaunchRejectedException("platform-response-invalid",
                "code2session returned neither openid nor anonymous_openid.");

        // session_key is sensitive (official security requirement): it is dropped here and
        // never leaves this adapter — the launch identity carries no secret material.
        var identity = !string.IsNullOrEmpty(openid) ? openid : AnonymousCredentialPrefix + anonymousOpenid;
        var canonicalLiveSessionKey = $"douyin-live:{_appId}:{identity}";
        return new VerifiedLaunch(PlatformName, request.AppId, identity, canonicalLiveSessionKey,
            request.LoginAttemptId, _clock());
    }

    private static string? ReadString(JsonElement root, string name)
        => root.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;
}

using System.Text.Json;
using Chorus.Adapters.Platforms;

namespace Chorus.Platform;

/// <summary>
/// Douyin mini-game launch adapter calling the officially verified code2Session OpenAPI
/// (docs/zh/reference/platform-douyin.md §2.2, verified 2026-10-14) through the shared
/// <see cref="DouyinMiniGameAuthClient"/> (review 2026-11-13 M7): one request/response
/// implementation for the OpenAPI, with this adapter supplying only the launch policy on
/// top — credential encoding, app binding, and the launch error categories. The launch
/// credential carries the client-side login code: a bare value is a tt.login <c>code</c>;
/// a value prefixed with <c>anonymous:</c> is an <c>anonymous_code</c>. The canonical live
/// session key is derived deterministically from the verified identity — one canonical live
/// session per streamer per app — because no official live-room binding API is verified yet.
/// AppId/AppSecret are injected via configuration and never appear in errors or logs.
/// </summary>
public sealed class DouyinMiniGameLaunchAdapter : IPlatformLaunchAdapter
{
    public const string PlatformName = "douyin";
    public const string AnonymousCredentialPrefix = "anonymous:";

    private readonly string _appId;
    private readonly DouyinMiniGameAuthClient _authClient;
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
        _authClient = new DouyinMiniGameAuthClient(new DouyinMiniGameOptions(appId, appSecret, apiBaseUrl), http);
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

        // The shared client owns the OpenAPI call: the request URI, the response parse,
        // and the fail-closed error extraction happen once for every douyin code2Session
        // consumer. Transport failures surface as HttpRequestException (non-2xx status or
        // network fault) and a non-JSON body as JsonException — mapped below.
        DouyinCode2SessionResult result;
        try
        {
            result = await _authClient.Code2SessionAsync(code, anonymousCode, cancellationToken).ConfigureAwait(false);
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
        catch (JsonException)
        {
            throw new PlatformLaunchRejectedException("platform-response-invalid",
                "The code2session response is not JSON.");
        }

        if (result.Success)
        {
            var session = result.Session!;
            // session_key is sensitive (official security requirement): it stays inside
            // the session record and never leaves this adapter — the launch identity
            // carries no secret material.
            var identity = !string.IsNullOrEmpty(session.Openid)
                ? session.Openid
                : AnonymousCredentialPrefix + session.AnonymousOpenid;
            var canonicalLiveSessionKey = $"douyin-live:{_appId}:{identity}";
            return new VerifiedLaunch(PlatformName, request.AppId, identity, canonicalLiveSessionKey,
                request.LoginAttemptId, _clock());
        }

        if (result.MalformedResponse)
        {
            throw new PlatformLaunchRejectedException("platform-response-invalid",
                "code2session returned neither openid nor anonymous_openid.");
        }

        throw new PlatformLaunchRejectedException("platform-rejected",
            $"code2session rejected the credential (error {result.ErrorCode}): {result.ErrorMessage ?? "unknown douyin error"}.");
    }
}

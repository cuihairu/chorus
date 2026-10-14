using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using Chorus.Adapters.Abstractions;
using Microsoft.Extensions.Logging;

namespace Chorus.Adapters.Platforms.MiniGame;

/// <summary>
/// Result of a server-side login code exchange: the platform account id plus an
/// optional opaque session key. The platform account id is the anchor of the
/// identity mapping (<see cref="MiniGameIdentityBinder"/>).
/// </summary>
public sealed record MiniGameLoginSession(string Platform, string PlatformAccountId, string? SessionKey = null);

/// <summary>
/// Wire contract for one platform's code2session endpoint.
///
/// Field names are configurable on purpose: the endpoint shape is only publicly
/// documented for platforms where it was verified, and guessing field names for
/// the rest would fabricate platform capabilities (todo §17).
/// </summary>
/// <param name="Platform">Platform key, e.g. "douyin" / "kuaishou".</param>
/// <param name="EndpointUrl">code2session endpoint; null → fail-closed.</param>
/// <param name="AppId">Platform app id; null → fail-closed.</param>
/// <param name="AppSecret">Platform app secret; null → fail-closed.</param>
/// <param name="CodeFormField">Form field carrying the login code.</param>
/// <param name="AppIdFormField">Form field carrying the app id.</param>
/// <param name="AppSecretFormField">Form field carrying the app secret.</param>
/// <param name="ResultField">Optional success-flag field name; null → no gate.</param>
/// <param name="ResultSuccessValue">Expected success-flag value (string compare).</param>
/// <param name="AccountIdField">Response field carrying the platform account id.</param>
/// <param name="SessionKeyField">Optional response field carrying the session key.</param>
public sealed record MiniGameLoginExchangeConfig(
    string Platform,
    string? EndpointUrl,
    string? AppId,
    string? AppSecret,
    string CodeFormField,
    string AppIdFormField,
    string AppSecretFormField,
    string? ResultField,
    string? ResultSuccessValue,
    string AccountIdField,
    string SessionKeyField)
{
    /// <summary>
    /// 快手「小程序」code2Session 官方口径（已核实，非小游戏）：
    /// https://open.kuaishou.com/docs/develop/server/code2Session.html
    /// 小游戏是否复用该端点未找到公开资料(待核)——见
    /// docs/reference/platform-kuaishou-mini-game.md。
    /// </summary>
    public static MiniGameLoginExchangeConfig KuaishouMiniProgram(string appId, string appSecret)
        => new(
            Platform: PlatformNames.Kuaishou,
            EndpointUrl: "https://open.kuaishou.com/oauth2/mp/code2session",
            AppId: appId,
            AppSecret: appSecret,
            CodeFormField: "js_code",
            AppIdFormField: "app_id",
            AppSecretFormField: "app_secret",
            ResultField: "result",
            ResultSuccessValue: "1",
            AccountIdField: "open_id",
            SessionKeyField: "session_key");
}

/// <summary>
/// Exchanges a mini-game login code for the platform account identity.
/// Implementations fail closed (return null) rather than throwing when
/// credentials or endpoints are not configured, so the caller rejects the login
/// visibly instead of treating a missing secret as success.
/// </summary>
public interface IMiniGameLoginExchange
{
    /// <summary>Exchange a login code; null when unresolvable (fail closed).</summary>
    Task<MiniGameLoginSession?> ExchangeAsync(string loginCode, CancellationToken cancellationToken = default);
}

/// <summary>
/// HTTP implementation of the login code exchange. The HttpClient is owned by
/// the caller (not disposed here). Field names come from
/// <see cref="MiniGameLoginExchangeConfig"/> so a platform whose endpoint shape
/// is not publicly documented must have its fields supplied explicitly.
/// </summary>
public sealed class HttpMiniGameLoginExchange : IMiniGameLoginExchange
{
    private const int MaxResponseBodyBytes = 16 * 1024;

    private readonly MiniGameLoginExchangeConfig _config;
    private readonly HttpClient _http;
    private readonly ILogger<HttpMiniGameLoginExchange>? _logger;
    private int _missingConfigLogged;

    public HttpMiniGameLoginExchange(
        MiniGameLoginExchangeConfig config,
        HttpClient? http = null,
        ILogger<HttpMiniGameLoginExchange>? logger = null)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _logger = logger;
    }

    public async Task<MiniGameLoginSession?> ExchangeAsync(string loginCode, CancellationToken cancellationToken = default)
    {
        if (!IsConfigured(out var reason))
        {
            if (Interlocked.Exchange(ref _missingConfigLogged, 1) == 0)
            {
                _logger?.LogError(
                    "{Platform} mini-game login exchange is not configured; all logins are rejected (fail closed): {Reason}",
                    _config.Platform, reason);
            }

            return null;
        }

        if (string.IsNullOrWhiteSpace(loginCode))
        {
            return null;
        }

        var form = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>(_config.CodeFormField!, loginCode.Trim()),
            new KeyValuePair<string, string>(_config.AppIdFormField!, _config.AppId!.Trim()),
            new KeyValuePair<string, string>(_config.AppSecretFormField!, _config.AppSecret!.Trim()),
        });

        HttpResponseMessage response;
        try
        {
            response = await _http.PostAsync(_config.EndpointUrl!, form, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return null;
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            if (response.Content.Headers.ContentLength.HasValue
                && response.Content.Headers.ContentLength > MaxResponseBodyBytes)
            {
                return null;
            }

            var body = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            return ParseResponse(body);
        }
    }

    private bool IsConfigured(out string? reason)
    {
        if (string.IsNullOrWhiteSpace(_config.EndpointUrl))
        {
            reason = "endpoint url 未配置";
            return false;
        }

        if (string.IsNullOrWhiteSpace(_config.AppId) || string.IsNullOrWhiteSpace(_config.AppSecret))
        {
            reason = "app id 或 app secret 未配置";
            return false;
        }

        reason = null;
        return true;
    }

    private MiniGameLoginSession? ParseResponse(byte[] body)
    {
        if (body.Length == 0 || body.Length > MaxResponseBodyBytes)
        {
            return null;
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            return null;
        }

        using (doc)
        {
            var root = doc.RootElement;

            if (IsPresent(_config.ResultField))
            {
                // 平台成功标志不匹配 = 换 session 失败（fail closed）。
                var flag = GetStringOrRaw(root, _config.ResultField!);
                if (!string.Equals(flag, _config.ResultSuccessValue, StringComparison.Ordinal))
                {
                    return null;
                }
            }

            var accountId = GetStringOrRaw(root, _config.AccountIdField);
            if (string.IsNullOrWhiteSpace(accountId))
            {
                return null;
            }

            var sessionKeyName = _config.SessionKeyField;
            var sessionKey = !string.IsNullOrWhiteSpace(sessionKeyName)
                ? GetStringOrRaw(root, sessionKeyName)?.Trim()
                : null;

            return new MiniGameLoginSession(
                Platform: _config.Platform,
                PlatformAccountId: accountId.Trim(),
                SessionKey: sessionKey);
        }
    }

    private static bool IsPresent(string? fieldName)
        => !string.IsNullOrWhiteSpace(fieldName);

    /// <summary>
    /// Reads a field as text: strings verbatim, numbers as raw text (platform
    /// success flags are commonly numeric, e.g. Kuaishou result = 1).
    /// </summary>
    private static string? GetStringOrRaw(JsonElement root, string fieldName)
    {
        if (!root.TryGetProperty(fieldName, out var element))
        {
            return null;
        }

        return element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : element.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False
                ? element.GetRawText()
                : null;
    }
}

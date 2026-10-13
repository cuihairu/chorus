using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Chorus.Adapters.Platforms;

/// <summary>
/// Connects to B站 open-live (blivedm <c>clients/open_live.py</c> + <c>ws_base.py</c>，
/// ✅ 原文核实)，normalizes nothing itself: every op-5 business message is re-POSTed
/// verbatim (signed with the x-bili-* scheme) to the chorus ingress endpoint, where
/// the shared face verifies / maps / dedups / tells. Transport seams (HttpMessageInvoker,
/// ws connect) are injectable so the reconnect state machine is testable offline.
///
/// 不硬编码绕过平台风控：不模拟 web 登录、不伪造设备指纹、不并发多连接压测。
/// </summary>
public sealed class BilibiliOpenLiveClient : IAsyncDisposable
{
    public const string DefaultStartUrl = "https://live-open.biliapi.com/v2/app_start";
    public const string DefaultHeartbeatUrl = "https://live-open.biliapi.com/v2/app/heartbeat";
    public const string DefaultEndUrl = "https://live-open.biliapi.com/v2/app/end";

    private static readonly TimeSpan DefaultGameHeartbeatInterval = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan DefaultWsHeartbeatInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DefaultReconnectMinDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan DefaultReconnectMaxDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan AuthReplyTimeout = TimeSpan.FromSeconds(10);

    private readonly BilibiliOpenLiveOptions _options;
    private readonly HttpMessageInvoker _http;
    private readonly Func<Uri, CancellationToken, Task<WebSocket>>? _connect;
    private readonly ILogger<BilibiliOpenLiveClient>? _logger;

    private readonly CancellationTokenSource _loopCts = new();
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private Task _loop = Task.CompletedTask;
    private string? _lastGameId;
    private bool _started;

    // 本轮会话是否已通过认证（RunOnce 内写、RunAsync 读，决定退避是否重置）。
    private bool _sessionEstablished;

    // 计数（host 侧只读快照）。
    public long AppStartCalls;
    public long Reconnects;
    public long AuthFailures;
    public long EventsPosted;
    public long PostFailures;

    public BilibiliOpenLiveClient(
        BilibiliOpenLiveOptions options,
        HttpMessageInvoker http,
        Func<Uri, CancellationToken, Task<WebSocket>>? connect = null,
        ILogger<BilibiliOpenLiveClient>? logger = null)
    {
        _options = options;
        _http = http;
        _connect = connect;
        _logger = logger;
    }

    public void Start()
    {
        if (_started)
        {
            return;
        }

        _started = true;
        _loop = RunAsync(_loopCts.Token);
    }

    public async Task StopAsync()
    {
        _loopCts.Cancel();
        try
        {
            await _loop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "bilibili open-live loop stopped with error");
        }

        await EndGameAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _sendGate.Dispose();
        _loopCts.Dispose();
        _http.Dispose();
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var delay = _options.ReconnectMinDelay ?? DefaultReconnectMinDelay;
        var maxDelay = _options.ReconnectMaxDelay ?? DefaultReconnectMaxDelay;
        var isReconnect = false;
        while (!ct.IsCancellationRequested)
        {
            if (isReconnect)
            {
                Interlocked.Increment(ref Reconnects);
            }

            _sessionEstablished = false;
            try
            {
                await RunOnceAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(
                    ex, "bilibili open-live session ended; backing off {DelayMs}ms before reconnect",
                    delay.TotalMilliseconds);
            }

            if (ct.IsCancellationRequested)
            {
                return;
            }

            isReconnect = true;
            try
            {
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            // 认证通过过的会话属网络类断开，重连从最小退避起；
            // 认证失败（凭据/链路问题）按 1s→2s→…→30s 指数退避。
            delay = _sessionEstablished
                ? _options.ReconnectMinDelay ?? DefaultReconnectMinDelay
                : TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds * 2, maxDelay.TotalMilliseconds));
        }
    }

    /// <summary>一轮完整会话：app_start → 连 wss → 认证 → 心跳 + 收流。以异常收尾（断连/关闭）。</summary>
    private async Task RunOnceAsync(CancellationToken ct)
    {
        var (wssLink, authBody, gameId) = await AppStartAsync(ct).ConfigureAwait(false);
        _lastGameId = gameId;

        using var ws = await ConnectAsync(new Uri(wssLink), ct).ConfigureAwait(false);

        // 认证：AUTH 包(auth_body)，等 AUTH_REPLY code==0（blivedm 行为）。
        await SendPacketAsync(ws, Encoding.UTF8.GetBytes(authBody), BilibiliOpenLiveProtocol.OperationAuth, ct)
            .ConfigureAwait(false);
        var authOk = await WaitAuthReplyAsync(ws, ct).ConfigureAwait(false);
        if (!authOk)
        {
            Interlocked.Increment(ref AuthFailures);
            throw new InvalidOperationException("bilibili open-live auth rejected (code != 0 or timeout)");
        }

        // 认证成功后立即心跳一次（blivedm 行为），随后固定间隔。
        _sessionEstablished = true;
        await SendPacketAsync(ws, "{}"u8.ToArray(), BilibiliOpenLiveProtocol.OperationHeartbeat, ct)
            .ConfigureAwait(false);

        using var innerCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var wsHeartbeat = HeartbeatLoopAsync(ws, innerCts.Token);
        var gameHeartbeat = GameHeartbeatLoopAsync(gameId, innerCts.Token);
        try
        {
            await ReceiveLoopAsync(ws, ct).ConfigureAwait(false);
            // 对端正常关闭也走重连（重取 wss_link，旧链路可能已失效）。
            throw new InvalidOperationException("bilibili open-live websocket closed");
        }
        finally
        {
            innerCts.Cancel();
            try
            {
                await Task.WhenAll(wsHeartbeat, gameHeartbeat).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    private async Task<(string WssLink, string AuthBody, string GameId)> AppStartAsync(CancellationToken ct)
    {
        var body = JsonSerializer.Serialize(new { code = _options.RoomOwnerAuthCode, app_id = _options.AppId });
        using var request = SignedRequest(HttpMethod.Post, _options.StartUrl ?? DefaultStartUrl, body);
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.TryGetProperty("code", out var codeEl)
            && codeEl.ValueKind == JsonValueKind.Number
            && codeEl.GetInt32() != 0)
        {
            throw new InvalidOperationException($"bilibili app_start failed, code={codeEl.GetInt32()}");
        }

        var data = root.GetProperty("data");
        var gameId = ReadGameId(data.GetProperty("game_info").GetProperty("game_id"));
        var wsInfo = data.GetProperty("websocket_info");
        var wssLink = wsInfo.GetProperty("wss_link")[0].GetString()
            ?? throw new InvalidOperationException("bilibili app_start returned empty wss_link");
        var authBody = wsInfo.GetProperty("auth_body").GetString()
            ?? throw new InvalidOperationException("bilibili app_start returned empty auth_body");

        Interlocked.Increment(ref AppStartCalls);
        return (wssLink, authBody, gameId);
    }

    private async Task<WebSocket> ConnectAsync(Uri uri, CancellationToken ct)
    {
        if (_connect != null)
        {
            return await _connect(uri, ct).ConfigureAwait(false);
        }

        var ws = new ClientWebSocket();
        await ws.ConnectAsync(uri, ct).ConfigureAwait(false);
        return ws;
    }

    private async Task<bool> WaitAuthReplyAsync(WebSocket ws, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(AuthReplyTimeout);
        while (!timeoutCts.Token.IsCancellationRequested)
        {
            var message = await ReceiveFullMessageAsync(ws, timeoutCts.Token).ConfigureAwait(false);
            if (message == null)
            {
                return false;
            }

            foreach (var (op, ver, body) in BilibiliOpenLiveProtocol.DecodeFrames(message))
            {
                if (op != BilibiliOpenLiveProtocol.OperationAuthReply)
                {
                    continue;
                }

                int code;
                try
                {
                    using var reply = JsonDocument.Parse(Encoding.UTF8.GetString(body));
                    code = reply.RootElement.TryGetProperty("code", out var codeValue)
                        && codeValue.ValueKind == JsonValueKind.Number
                            ? codeValue.GetInt32()
                            : -1;
                }
                catch (JsonException)
                {
                    return false;
                }

                return code == 0;
            }
        }

        return false;
    }

    private async Task HeartbeatLoopAsync(WebSocket ws, CancellationToken ct)
    {
        var interval = _options.WsHeartbeatInterval ?? DefaultWsHeartbeatInterval;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, ct).ConfigureAwait(false);
                await SendPacketAsync(ws, "{}"u8.ToArray(), BilibiliOpenLiveProtocol.OperationHeartbeat, ct)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                // 心跳失败由接收循环的断连路径兜底重连，这里只记录。
                _logger?.LogDebug(ex, "bilibili open-live ws heartbeat send failed");
                return;
            }
        }
    }

    private async Task GameHeartbeatLoopAsync(string gameId, CancellationToken ct)
    {
        var interval = _options.GameHeartbeatInterval ?? DefaultGameHeartbeatInterval;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, ct).ConfigureAwait(false);
                var body = JsonSerializer.Serialize(new { game_id = gameId });
                using var request = SignedRequest(HttpMethod.Post, _options.HeartbeatUrl ?? DefaultHeartbeatUrl, body);
                using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    _logger?.LogWarning(
                        "bilibili open-live game heartbeat failed: {Status}", (int)response.StatusCode);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "bilibili open-live game heartbeat error");
                return;
            }
        }
    }

    private async Task ReceiveLoopAsync(WebSocket ws, CancellationToken ct)
    {
        while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            var message = await ReceiveFullMessageAsync(ws, ct).ConfigureAwait(false);
            if (message == null)
            {
                return;
            }

            foreach (var (op, ver, body) in BilibiliOpenLiveProtocol.DecodeFrames(message))
            {
                if (op != BilibiliOpenLiveProtocol.OperationMessage)
                {
                    // 心跳回复（op 3）等其余 op 忽略。
                    continue;
                }

                var json = Encoding.UTF8.GetString(body);
                await PostEventAsync(json, ct).ConfigureAwait(false);
            }
        }
    }

    private async Task PostEventAsync(string json, CancellationToken ct)
    {
        try
        {
            using var request = SignedRequest(HttpMethod.Post, _options.IngressUrl, json);
            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                Interlocked.Increment(ref EventsPosted);
            }
            else
            {
                Interlocked.Increment(ref PostFailures);
                _logger?.LogWarning(
                    "bilibili open-live event post rejected: {Status} body={Body}",
                    (int)response.StatusCode, json);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Interlocked.Increment(ref PostFailures);
            _logger?.LogWarning(ex, "bilibili open-live event post failed");
        }
    }

    private async Task EndGameAsync()
    {
        var gameId = Interlocked.Exchange(ref _lastGameId, null);
        if (gameId == null || _options.EndUrl == string.Empty)
        {
            return;
        }

        try
        {
            var body = JsonSerializer.Serialize(new { game_id = gameId });
            using var request = SignedRequest(HttpMethod.Post, _options.EndUrl ?? DefaultEndUrl, body);
            using var response = await _http.SendAsync(request, CancellationToken.None).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger?.LogWarning("bilibili open-live app/end failed: {Status}", (int)response.StatusCode);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "bilibili open-live app/end error (ignored)");
        }
    }

    private HttpRequestMessage SignedRequest(HttpMethod method, string url, string body)
    {
        var request = new HttpRequestMessage(method, url)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

        var headers = BilibiliSigner.BuildHeaders(
            body, _options.AccessKeyId, _options.AccessKeySecret,
            DateTimeOffset.UtcNow.ToUnixTimeSeconds(), Guid.NewGuid().ToString("N"));
        foreach (var (name, value) in headers)
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }

        return request;
    }

    private async Task SendPacketAsync(WebSocket ws, byte[] body, int operation, CancellationToken ct)
    {
        var packet = BilibiliOpenLiveProtocol.MakePacket(body, operation);
        await _sendGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await ws.SendAsync(packet, WebSocketMessageType.Binary, true, ct).ConfigureAwait(false);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    /// <summary>收满一条消息（处理分片）；对端关闭返回 null。</summary>
    private static async Task<byte[]?> ReceiveFullMessageAsync(WebSocket ws, CancellationToken ct)
    {
        using var message = new MemoryStream();
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var result = await ws.ReceiveAsync(buffer, ct).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }

            message.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
            {
                return message.ToArray();
            }
        }
    }

    private static string ReadGameId(JsonElement element)
        => element.ValueKind switch
        {
            JsonValueKind.String => element.GetString() ?? string.Empty,
            JsonValueKind.Number => element.GetInt64().ToString(System.Globalization.CultureInfo.InvariantCulture),
            _ => throw new InvalidOperationException("bilibili app_start game_id has unexpected type"),
        };
}

/// <summary>
/// open-live 接入参数。凭据四件套由主播授权、走配置注入，不进仓不进日志。
/// </summary>
public sealed record BilibiliOpenLiveOptions(
    string AccessKeyId,
    string AccessKeySecret,
    long AppId,
    string RoomOwnerAuthCode,
    string IngressUrl,
    string? StartUrl = null,
    string? HeartbeatUrl = null,
    string? EndUrl = null,
    TimeSpan? GameHeartbeatInterval = null,
    TimeSpan? WsHeartbeatInterval = null,
    TimeSpan? ReconnectMinDelay = null,
    TimeSpan? ReconnectMaxDelay = null);

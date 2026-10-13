using System.Diagnostics;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Chorus.Adapters.Platforms;

namespace Chorus.Engine.Tests;

/// <summary>
/// BilibiliOpenLiveClient state machine over injected transports (offline
/// golden run, todo §12 P1): app_start → wss auth (op 7 / auth_body) → op-5
/// business messages re-POSTed signed to the ingress URL; drop → exponential
/// backoff reconnect that re-fetches wss_link; auth rejection counted then
/// retried; graceful stop posts app/end.
/// </summary>
public sealed class BilibiliOpenLiveClientTests
{
    private const string KeyId = "test-key";
    private const string Secret = "test-secret";
    private const string AuthBodyJson = """{"uid":123,"roomid":456,"proto_ver":2}""";
    private const string DanmuJson =
        """{"cmd":"DANMU_MSG","msg_id":"evt-1","data":{"open_id":"ou-9","msg":"test-danmaku"}}""";

    private static string AppStartJson(string gameId = "g-1") =>
        "{\"code\":0,\"data\":{\"game_info\":{\"game_id\":\"" + gameId
        + "\"},\"websocket_info\":{\"wss_link\":[\"ws://bilibili.test/live\"],\"auth_body\":"
        + JsonSerializer.Serialize(AuthBodyJson) + "}}}";

    private static BilibiliOpenLiveOptions Options() => new(
        AccessKeyId: KeyId,
        AccessKeySecret: Secret,
        AppId: 1001,
        RoomOwnerAuthCode: "auth-code",
        IngressUrl: "http://test/ingress/bilibili",
        StartUrl: "http://test/app_start",
        HeartbeatUrl: "http://test/app/heartbeat",
        EndUrl: "http://test/app/end",
        ReconnectMinDelay: TimeSpan.FromMilliseconds(10),
        ReconnectMaxDelay: TimeSpan.FromMilliseconds(50));

    /// <summary>按 URL 分发的 open-live HTTP 桩：记录全部请求，ingress 可断言签名。</summary>
    private sealed class FakeOpenLiveHttp : HttpMessageInvoker
    {
        private readonly object _gate = new();
        private readonly string _appStartJson;
        public List<(string Path, string Body, IReadOnlyDictionary<string, string> Headers)> Requests { get; } = new();
        public List<string> IngressBodies { get; } = new();

        public FakeOpenLiveHttp(string appStartJson)
            : base(new HttpClientHandler())
        {
            _appStartJson = appStartJson;
        }

        public override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content?.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult() ?? string.Empty;
            var headers = request.Headers.ToDictionary(
                h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
            lock (_gate)
            {
                Requests.Add((request.RequestUri!.AbsolutePath, body, headers));
                if (request.RequestUri.AbsolutePath.EndsWith("ingress/bilibili"))
                {
                    IngressBodies.Add(body);
                }
            }

            var response = request.RequestUri.AbsolutePath switch
            {
                var p when p.EndsWith("app_start") => _appStartJson,
                _ => "{}",
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response, Encoding.UTF8, "application/json"),
            });
        }
    }

    /// <summary>脚本化对端 wss：Push 投喂下行帧，SentByClient 记录上行，CloseRemote 模拟断连。</summary>
    private sealed class ScriptedRemoteSocket : WebSocket
    {
        private readonly object _gate = new();
        private readonly Queue<byte[]> _inbound = new();
        private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<byte[]> SentByClient { get; } = new();

        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => _closed.Task.IsCompleted ? WebSocketState.Closed : WebSocketState.Open;
        public override string? SubProtocol => null;

        public void Push(byte[] frame)
        {
            lock (_gate)
            {
                _inbound.Enqueue(frame);
            }
        }

        public void CloseRemote() => _closed.TrySetResult();

        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        {
            lock (SentByClient)
            {
                SentByClient.Add(buffer.ToArray());
            }

            return Task.CompletedTask;
        }

        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            // 必须真异步：空队列时让出线程（Task.Delay 轮询），否则客户端 await
            // 到的是同步阻塞实现，会把调用方线程（测试线程）卡死在 Start() 里。
            var frame = await DequeueAsync(cancellationToken).ConfigureAwait(false);
            if (frame == null)
            {
                // 取消/关闭等价于对端断连。
                return new WebSocketReceiveResult(
                    0, WebSocketMessageType.Close, true, WebSocketCloseStatus.NormalClosure, "remote closed");
            }

            frame.CopyTo(buffer.Array!, buffer.Offset);
            return new WebSocketReceiveResult(frame.Length, WebSocketMessageType.Binary, true);
        }

        private async Task<byte[]?> DequeueAsync(CancellationToken cancellationToken)
        {
            while (true)
            {
                lock (_gate)
                {
                    if (_inbound.Count > 0)
                    {
                        return _inbound.Dequeue();
                    }

                    if (_closed.Task.IsCompleted)
                    {
                        return null;
                    }
                }

                try
                {
                    await Task.Delay(50, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return null;
                }
            }
        }

        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        {
            _closed.TrySetResult();
            return Task.CompletedTask;
        }

        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public override void Abort()
        {
        }

        public override void Dispose()
        {
        }
    }

    private static byte[] AuthReplyOkPacket()
        => BilibiliOpenLiveProtocol.MakePacket(
            Encoding.UTF8.GetBytes("""{"code":0}"""), BilibiliOpenLiveProtocol.OperationAuthReply);

    private static byte[] AuthReplyRejectPacket()
        => BilibiliOpenLiveProtocol.MakePacket(
            Encoding.UTF8.GetBytes("""{"code":-101,"message":"token error"}"""),
            BilibiliOpenLiveProtocol.OperationAuthReply);

    private static byte[] MessagePacket(string json)
        => BilibiliOpenLiveProtocol.MakePacket(
            Encoding.UTF8.GetBytes(json), BilibiliOpenLiveProtocol.OperationMessage);

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(10);
        }
    }

    private static (int Operation, byte[] Body) ParseSentPacket(byte[] packet)
    {
        var frames = BilibiliOpenLiveProtocol.ParseFrames(packet).ToList();
        var frame = Assert.Single(frames);
        return (frame.Operation, frame.Body);
    }

    [Fact]
    public async Task AppStart_ConnectsAuths_AndForwardsEventsSigned()
    {
        var http = new FakeOpenLiveHttp(AppStartJson());
        var remote = new ScriptedRemoteSocket();
        remote.Push(AuthReplyOkPacket());
        remote.Push(MessagePacket(DanmuJson));
        await using var client = new BilibiliOpenLiveClient(
            Options(), http, connect: (_, _) => Task.FromResult<WebSocket>(remote));
        client.Start();

        await WaitUntilAsync(() => Volatile.Read(ref client.EventsPosted) >= 1);

        // 上行第一包是 AUTH(auth_body 原文)，随后立即心跳（blivedm 行为）。
        List<byte[]> sent;
        lock (remote.SentByClient)
        {
            sent = remote.SentByClient.ToList();
        }

        Assert.True(sent.Count >= 2, $"client sent {sent.Count} packets");
        var (authOp, authBody) = ParseSentPacket(sent[0]);
        Assert.Equal(BilibiliOpenLiveProtocol.OperationAuth, authOp);
        Assert.Equal(AuthBodyJson, Encoding.UTF8.GetString(authBody));
        var (heartbeatOp, heartbeatBody) = ParseSentPacket(sent[1]);
        Assert.Equal(BilibiliOpenLiveProtocol.OperationHeartbeat, heartbeatOp);
        Assert.Equal("{}", Encoding.UTF8.GetString(heartbeatBody));

        // 事件原样（op-5 包体）签名转发到 ingress。
        Assert.Contains(DanmuJson, http.IngressBodies);
        var ingress = http.Requests.Single(r => r.Path.EndsWith("ingress/bilibili"));
        Assert.True(BilibiliSigner.VerifyHeaders(
            DanmuJson, KeyId, Secret, ingress.Headers, DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
        // app_start 请求本身也带签名头。
        var appStart = http.Requests.Single(r => r.Path.EndsWith("app_start"));
        Assert.Contains("x-bili-accesskeyid", ingress.Headers.Keys.Select(k => k.ToLowerInvariant()));
        Assert.True(appStart.Headers.ContainsKey(BilibiliSigner.AuthorizationHeader));
        Assert.Equal(0, Volatile.Read(ref client.PostFailures));
    }

    [Fact]
    public async Task Reconnect_AfterDrop_RefetchesAppStart()
    {
        var http = new FakeOpenLiveHttp(AppStartJson());
        var remote1 = new ScriptedRemoteSocket();
        var remote2 = new ScriptedRemoteSocket();
        var index = 0;
        await using var client = new BilibiliOpenLiveClient(
            Options(), http, connect: (_, _) => Task.FromResult<WebSocket>(Interlocked.Increment(ref index) == 1 ? remote1 : remote2));
        remote1.Push(AuthReplyOkPacket());
        remote1.Push(MessagePacket(DanmuJson));
        client.Start();

        await WaitUntilAsync(() => Volatile.Read(ref client.EventsPosted) >= 1);
        remote1.CloseRemote();

        await WaitUntilAsync(() => Volatile.Read(ref client.AppStartCalls) >= 2);
        remote2.Push(AuthReplyOkPacket());

        await WaitUntilAsync(() => remote2.SentByClient.Count >= 1, timeoutMs: 5000);

        Assert.True(Volatile.Read(ref client.Reconnects) >= 1, "reconnect not counted");
        Assert.True(Volatile.Read(ref client.AppStartCalls) >= 2, "wss_link not re-fetched");
        var (op, body) = ParseSentPacket(remote2.SentByClient[0]);
        Assert.Equal(BilibiliOpenLiveProtocol.OperationAuth, op);
        Assert.Equal(AuthBodyJson, Encoding.UTF8.GetString(body));
    }

    [Fact]
    public async Task AuthRejected_CountsFailure_AndRetries()
    {
        var http = new FakeOpenLiveHttp(AppStartJson());
        var remote1 = new ScriptedRemoteSocket();
        var remote2 = new ScriptedRemoteSocket();
        var index = 0;
        await using var client = new BilibiliOpenLiveClient(
            Options(), http, connect: (_, _) => Task.FromResult<WebSocket>(Interlocked.Increment(ref index) == 1 ? remote1 : remote2));
        remote1.Push(AuthReplyRejectPacket());
        remote2.Push(AuthReplyOkPacket());
        client.Start();

        await WaitUntilAsync(() => Volatile.Read(ref client.AuthFailures) >= 1);
        await WaitUntilAsync(() => remote2.SentByClient.Count >= 1);

        Assert.True(Volatile.Read(ref client.AppStartCalls) >= 2);
        var (op, _) = ParseSentPacket(remote2.SentByClient[0]);
        Assert.Equal(BilibiliOpenLiveProtocol.OperationAuth, op);
    }

    [Fact]
    public async Task Stop_PostsAppEnd_WithGameId()
    {
        var http = new FakeOpenLiveHttp(AppStartJson(gameId: "g-end"));
        var remote = new ScriptedRemoteSocket();
        remote.Push(AuthReplyOkPacket());
        var client = new BilibiliOpenLiveClient(
            Options(), http, connect: (_, _) => Task.FromResult<WebSocket>(remote));
        client.Start();
        await WaitUntilAsync(() => Volatile.Read(ref client.AppStartCalls) >= 1);

        await client.DisposeAsync();

        var end = http.Requests.FirstOrDefault(r => r.Path.EndsWith("app/end"));
        Assert.NotNull(end);
        Assert.Contains("\"g-end\"", end.Body);
        Assert.True(BilibiliSigner.VerifyHeaders(
            end.Body, KeyId, Secret, end.Headers, DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
    }
}

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

    private static string AppStartJsonWithNumberGameId() =>
        "{\"code\":0,\"data\":{\"game_info\":{\"game_id\":12345},\"websocket_info\":{\"wss_link\":[\"ws://bilibili.test/live\"],\"auth_body\":"
        + JsonSerializer.Serialize(AuthBodyJson) + "}}}";

    private static BilibiliOpenLiveOptions HeartbeatOptions() => new(
        AccessKeyId: KeyId,
        AccessKeySecret: Secret,
        AppId: 1001,
        RoomOwnerAuthCode: "auth-code",
        IngressUrl: "http://test/ingress/bilibili",
        StartUrl: "http://test/app_start",
        HeartbeatUrl: "http://test/app/heartbeat",
        EndUrl: "http://test/app/end",
        GameHeartbeatInterval: TimeSpan.FromMilliseconds(30),
        WsHeartbeatInterval: TimeSpan.FromMilliseconds(30),
        ReconnectMinDelay: TimeSpan.FromMilliseconds(10),
        ReconnectMaxDelay: TimeSpan.FromMilliseconds(50));

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

        public HttpStatusCode AppStartStatus { get; set; } = HttpStatusCode.OK;
        public HttpStatusCode HeartbeatStatus { get; set; } = HttpStatusCode.OK;
        public HttpStatusCode EndStatus { get; set; } = HttpStatusCode.OK;
        public HttpStatusCode IngressStatus { get; set; } = HttpStatusCode.OK;
        public bool IngressThrow { get; set; }
        public bool HeartbeatThrow { get; set; }
        public bool EndThrow { get; set; }

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

            if (request.RequestUri.AbsolutePath.EndsWith("ingress/bilibili") && IngressThrow)
            {
                throw new HttpRequestException("stub ingress down");
            }

            if (request.RequestUri.AbsolutePath.EndsWith("app/heartbeat") && HeartbeatThrow)
            {
                throw new HttpRequestException("stub heartbeat down");
            }

            if (request.RequestUri.AbsolutePath.EndsWith("app/end") && EndThrow)
            {
                throw new HttpRequestException("stub end down");
            }

            var (response, status) = request.RequestUri.AbsolutePath switch
            {
                var p when p.EndsWith("app_start") => (_appStartJson, AppStartStatus),
                var p when p.EndsWith("app/heartbeat") => ("{}", HeartbeatStatus),
                var p when p.EndsWith("app/end") => ("{}", EndStatus),
                _ => ("{}", IngressStatus),
            };
            return Task.FromResult(new HttpResponseMessage(status)
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

        /// <summary>0 = 永不失败；&gt;0 时第 N 包起的发送抛传输层异常（桩故障）。</summary>
        public int SendFailsAfter;

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
                if (SendFailsAfter > 0 && SentByClient.Count >= SendFailsAfter)
                {
                    throw new InvalidOperationException("stub transport down");
                }
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
        Assert.NotEqual(default, end);
        Assert.Contains("\"g-end\"", end.Body);
        Assert.True(BilibiliSigner.VerifyHeaders(
            end.Body, KeyId, Secret, end.Headers, DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
    }

    [Fact]
    public async Task HeartbeatLoops_KeepWsAndGameHeartbeatsAlive()
    {
        var http = new FakeOpenLiveHttp(AppStartJson());
        var remote = new ScriptedRemoteSocket();
        remote.Push(AuthReplyOkPacket());
        // 心跳回复（op 3）在接收循环里必须被忽略而不是当作事件转发。
        remote.Push(BilibiliOpenLiveProtocol.MakePacket(
            """{"popularity":7}"""u8.ToArray(), BilibiliOpenLiveProtocol.OperationHeartbeatReply));
        remote.Push(MessagePacket(DanmuJson));
        await using var client = new BilibiliOpenLiveClient(
            HeartbeatOptions(), http, connect: (_, _) => Task.FromResult<WebSocket>(remote));
        client.Start();

        // auth + 立即心跳 + 至少一次循环心跳（30ms 间隔）。
        await WaitUntilAsync(() => remote.SentByClient.Count >= 3);
        // 游戏心跳按 interval POST game_id。
        await WaitUntilAsync(() => http.Requests.Any(r => r.Path.EndsWith("app/heartbeat")));
        var gameHeartbeat = http.Requests.First(r => r.Path.EndsWith("app/heartbeat"));
        Assert.Contains("\"g-1\"", gameHeartbeat.Body);

        // 事件仍被转发（接收循环没被心跳回复卡死）。
        await WaitUntilAsync(() => Volatile.Read(ref client.EventsPosted) >= 1);
        Assert.Equal(0, Volatile.Read(ref client.PostFailures));
    }

    [Fact]
    public async Task GameHeartbeatNonSuccess_DoesNotKillLoop()
    {
        var http = new FakeOpenLiveHttp(AppStartJson()) { HeartbeatStatus = HttpStatusCode.InternalServerError };
        var remote = new ScriptedRemoteSocket();
        remote.Push(AuthReplyOkPacket());
        await using var client = new BilibiliOpenLiveClient(
            HeartbeatOptions(), http, connect: (_, _) => Task.FromResult<WebSocket>(remote));
        client.Start();

        // 500 只记日志：心跳循环继续打点（多次请求）。
        await WaitUntilAsync(() => http.Requests.Count(r => r.Path.EndsWith("app/heartbeat")) >= 2);
    }

    [Fact]
    public async Task AuthReplyMalformedJson_CountsFailure_AndRetries()
    {
        var http = new FakeOpenLiveHttp(AppStartJson());
        var remote1 = new ScriptedRemoteSocket();
        var remote2 = new ScriptedRemoteSocket();
        var index = 0;
        await using var client = new BilibiliOpenLiveClient(
            Options(), http, connect: (_, _) => Task.FromResult<WebSocket>(Interlocked.Increment(ref index) == 1 ? remote1 : remote2));
        remote1.Push(BilibiliOpenLiveProtocol.MakePacket(
            "{not json"u8.ToArray(), BilibiliOpenLiveProtocol.OperationAuthReply));
        remote2.Push(AuthReplyOkPacket());
        client.Start();

        await WaitUntilAsync(() => Volatile.Read(ref client.AuthFailures) >= 1);
        await WaitUntilAsync(() => remote2.SentByClient.Count >= 1);

        var (op, _) = ParseSentPacket(remote2.SentByClient[0]);
        Assert.Equal(BilibiliOpenLiveProtocol.OperationAuth, op);
    }

    [Fact]
    public async Task AppStartErrorCode_BacksoffWithoutGameId()
    {
        var http = new FakeOpenLiveHttp("""{"code":-101,"message":"auth code expired"}""");
        var remote = new ScriptedRemoteSocket();
        remote.Push(AuthReplyOkPacket());
        var client = new BilibiliOpenLiveClient(
            Options(), http, connect: (_, _) => Task.FromResult<WebSocket>(remote));
        client.Start();

        // 重试在跑（多次 app_start），但 code!=0 从不计成功、game_id 永远缺位。
        await WaitUntilAsync(() => http.Requests.Count(r => r.Path.EndsWith("app_start")) >= 2);
        Assert.Equal(0, Volatile.Read(ref client.AppStartCalls));

        await client.DisposeAsync();
        // 从未拿到 game_id：不 POST app/end。
        Assert.DoesNotContain(http.Requests, r => r.Path.EndsWith("app/end"));
    }

    [Fact]
    public async Task AppStartServerError_Backsoff()
    {
        var http = new FakeOpenLiveHttp(AppStartJson()) { AppStartStatus = HttpStatusCode.ServiceUnavailable };
        var remote = new ScriptedRemoteSocket();
        remote.Push(AuthReplyOkPacket());
        await using var client = new BilibiliOpenLiveClient(
            Options(), http, connect: (_, _) => Task.FromResult<WebSocket>(remote));
        client.Start();

        await WaitUntilAsync(() => http.Requests.Count(r => r.Path.EndsWith("app_start")) >= 2);
        Assert.Equal(0, Volatile.Read(ref client.AppStartCalls));
    }

    [Fact]
    public async Task AppStartNullWssLink_RejectedThenRetried()
    {
        // 第一次返回 wss_link:[null]（fail-visible 守卫），第二次正常 → 客户端应恢复。
        var http = new FakeOpenLiveHttp(
            """{"code":0,"data":{"game_info":{"game_id":"g-1"},"websocket_info":{"wss_link":[null],"auth_body":{"uid":1}}}}""");
        var remote = new ScriptedRemoteSocket();
        remote.Push(AuthReplyOkPacket());
        await using var client = new BilibiliOpenLiveClient(
            Options(), http, connect: (_, _) => Task.FromResult<WebSocket>(remote));
        client.Start();

        await WaitUntilAsync(() => http.Requests.Count(r => r.Path.EndsWith("app_start")) >= 2);
        // 第二次响应正常（桩固定返回同一 JSON，这里只断言重试在进行且未崩溃）。
        Assert.True(Volatile.Read(ref client.Reconnects) >= 1 || http.Requests.Count >= 2);
    }

    [Fact]
    public async Task NumericGameId_FlowsToAppEnd()
    {
        var http = new FakeOpenLiveHttp(AppStartJsonWithNumberGameId());
        var remote = new ScriptedRemoteSocket();
        remote.Push(AuthReplyOkPacket());
        var client = new BilibiliOpenLiveClient(
            Options(), http, connect: (_, _) => Task.FromResult<WebSocket>(remote));
        client.Start();
        await WaitUntilAsync(() => Volatile.Read(ref client.AppStartCalls) >= 1);

        await client.DisposeAsync();

        var end = http.Requests.FirstOrDefault(r => r.Path.EndsWith("app/end"));
        Assert.NotEqual(default, end);
        // game_id 数字型（12345）→ app/end 体里同样序列化为数字。
        Assert.Contains("12345", end.Body);
    }

    [Fact]
    public async Task Dispose_Twice_IsNoOp()
    {
        // 显式 DisposeAsync 与 await using 双路径（同 SessionRegistry 纪律）：第二次必须无副作用。
        var http = new FakeOpenLiveHttp(AppStartJson());
        var client = new BilibiliOpenLiveClient(
            Options(), http, connect: (_, _) => Task.FromResult<WebSocket>(new ScriptedRemoteSocket()));
        client.Start();

        await client.DisposeAsync();
        await client.DisposeAsync();

        Assert.True(http.Requests.Count(r => r.Path.EndsWith("app/end")) <= 1);
    }

    [Fact]
    public async Task IngressRejected_CountsPostFailure()
    {
        var http = new FakeOpenLiveHttp(AppStartJson()) { IngressStatus = HttpStatusCode.ServiceUnavailable };
        var remote = new ScriptedRemoteSocket();
        remote.Push(AuthReplyOkPacket());
        remote.Push(MessagePacket(DanmuJson));
        await using var client = new BilibiliOpenLiveClient(
            Options(), http, connect: (_, _) => Task.FromResult<WebSocket>(remote));
        client.Start();

        await WaitUntilAsync(() => Volatile.Read(ref client.PostFailures) >= 1);
        Assert.Equal(0, Volatile.Read(ref client.EventsPosted));
    }

    [Fact]
    public async Task IngressDown_CountsPostFailure()
    {
        var http = new FakeOpenLiveHttp(AppStartJson()) { IngressThrow = true };
        var remote = new ScriptedRemoteSocket();
        remote.Push(AuthReplyOkPacket());
        remote.Push(MessagePacket(DanmuJson));
        await using var client = new BilibiliOpenLiveClient(
            Options(), http, connect: (_, _) => Task.FromResult<WebSocket>(remote));
        client.Start();

        await WaitUntilAsync(() => Volatile.Read(ref client.PostFailures) >= 1);
        Assert.Equal(0, Volatile.Read(ref client.EventsPosted));
    }

    [Fact]
    public async Task AppEndFailure_IsIgnoredOnStop()
    {
        var http = new FakeOpenLiveHttp(AppStartJson()) { EndStatus = HttpStatusCode.InternalServerError };
        var remote = new ScriptedRemoteSocket();
        remote.Push(AuthReplyOkPacket());
        var client = new BilibiliOpenLiveClient(
            Options(), http, connect: (_, _) => Task.FromResult<WebSocket>(remote));
        client.Start();
        await WaitUntilAsync(() => Volatile.Read(ref client.AppStartCalls) >= 1);

        // app/end 500 只记日志，关停不抛。
        await client.DisposeAsync();
        Assert.Contains(http.Requests, r => r.Path.EndsWith("app/end"));
    }

    [Fact]
    public async Task Start_Twice_RunsOneLoop()
    {
        // 二次 Start 守卫：单循环 parked 在 connect 时，第二个循环若真启动会在
        // 退避窗口内再次 app_start —— 计数必须钉死在 1。
        var http = new FakeOpenLiveHttp(AppStartJson());
        var gate = new TaskCompletionSource<WebSocket>(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new BilibiliOpenLiveClient(
            Options(), http, connect: (_, _) => gate.Task);
        client.Start();
        client.Start();

        await WaitUntilAsync(() => Volatile.Read(ref client.AppStartCalls) >= 1);
        await Task.Delay(120); // > 最大退避 50ms：双循环必然已再次 app_start

        Assert.Equal(1, Volatile.Read(ref client.AppStartCalls));

        // 收尾：放行 connect（空 socket），停机不留悬挂任务。
        gate.SetResult(new ScriptedRemoteSocket());
        await client.DisposeAsync();
    }

    [Fact]
    public async Task ConnectCancelled_LoopEndsQuietly_OnStop()
    {
        // connect 在循环令牌上抛取消：OCE 必须走 RunAsync 的取消臂正常收尾
        // （不是故障退避），且 StopAsync 等到循环退出后才算停干净。
        var http = new FakeOpenLiveHttp(AppStartJson());
        var gate = new TaskCompletionSource<WebSocket>(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new BilibiliOpenLiveClient(
            Options(), http, connect: (_, ct) => ParkUntilCancelledAsync(gate, ct));
        client.Start();

        await WaitUntilAsync(() => Volatile.Read(ref client.AppStartCalls) >= 1);

        await client.StopAsync();

        Assert.Equal(0, Volatile.Read(ref client.Reconnects));
        Assert.Equal(1, Volatile.Read(ref client.AppStartCalls));
        // game_id 已在 app_start 拿到：走干净停机路径会补发 app/end。
        Assert.Contains(http.Requests, r => r.Path.EndsWith("app/end"));
    }

    [Fact]
    public async Task WsHeartbeatSendFault_StopsHeartbeat_ReceiveLoopSurvives()
    {
        // 传输层 Send 抛非取消异常：ws 心跳循环只记日志退出，接收循环不动
        // （断连兜底交给接收循环的 null 帧路径，不靠心跳）。
        var http = new FakeOpenLiveHttp(AppStartJson());
        var remote = new ScriptedRemoteSocket { SendFailsAfter = 3 };
        remote.Push(AuthReplyOkPacket());
        await using var client = new BilibiliOpenLiveClient(
            HeartbeatOptions(), http, connect: (_, _) => Task.FromResult<WebSocket>(remote));
        client.Start();

        // auth(1) + 认证后立即心跳(2) 都成功，首个间隔心跳(3) 失败退出。
        await WaitUntilAsync(() => remote.SentByClient.Count >= 3);

        // 心跳循环已死：多个 30ms 间隔后不再有新包。
        var frozen = remote.SentByClient.Count;
        await Task.Delay(150);
        Assert.Equal(frozen, remote.SentByClient.Count);

        // 接收循环仍活着：后续事件照常转发。
        remote.Push(MessagePacket(DanmuJson));
        await WaitUntilAsync(() => Volatile.Read(ref client.EventsPosted) >= 1);
    }

    [Fact]
    public async Task GameHeartbeatFault_StopsGameHeartbeat_SessionSurvives()
    {
        // 游戏心跳 HTTP 抛非取消异常：该循环记日志退出；ws 心跳与收流不受牵连。
        var http = new FakeOpenLiveHttp(AppStartJson()) { HeartbeatThrow = true };
        var remote = new ScriptedRemoteSocket();
        remote.Push(AuthReplyOkPacket());
        await using var client = new BilibiliOpenLiveClient(
            HeartbeatOptions(), http, connect: (_, _) => Task.FromResult<WebSocket>(remote));
        client.Start();

        // 第一拍就失败（间隔 30ms）：等多个间隔，心跳请求数必须钉死。
        await WaitUntilAsync(() => http.Requests.Any(r => r.Path.EndsWith("app/heartbeat")));
        var count = http.Requests.Count(r => r.Path.EndsWith("app/heartbeat"));
        await Task.Delay(150);
        Assert.Equal(count, http.Requests.Count(r => r.Path.EndsWith("app/heartbeat")));

        // 会话本身还活着：收流继续。
        remote.Push(MessagePacket(DanmuJson));
        await WaitUntilAsync(() => Volatile.Read(ref client.EventsPosted) >= 1);
    }

    [Fact]
    public async Task AppEndThrow_IsIgnoredOnStop()
    {
        // app/end 传输层抛异常（区别于 5xx）：一样只记日志，关停不抛。
        var http = new FakeOpenLiveHttp(AppStartJson()) { EndThrow = true };
        var remote = new ScriptedRemoteSocket();
        remote.Push(AuthReplyOkPacket());
        var client = new BilibiliOpenLiveClient(
            Options(), http, connect: (_, _) => Task.FromResult<WebSocket>(remote));
        client.Start();
        await WaitUntilAsync(() => Volatile.Read(ref client.AppStartCalls) >= 1);

        await client.DisposeAsync();

        Assert.Contains(http.Requests, r => r.Path.EndsWith("app/end"));
        Assert.Equal(0, Volatile.Read(ref client.Reconnects));
    }

    [Fact]
    public async Task AppStartInvalidGameIdType_BacksoffWithoutGameId()
    {
        // game_id 非字符串/数字（fail-visible 守卫的最后一臂）：app_start 整轮
        // 失败退避重试，game_id 永不落地（AppStartCalls==0），关停不打 app/end。
        var http = new FakeOpenLiveHttp(
            """{"code":0,"data":{"game_info":{"game_id":true},"websocket_info":{"wss_link":["ws://bilibili.test/live"],"auth_body":{"uid":1}}}}""");
        var remote = new ScriptedRemoteSocket();
        var client = new BilibiliOpenLiveClient(
            Options(), http, connect: (_, _) => Task.FromResult<WebSocket>(remote));
        client.Start();

        await WaitUntilAsync(() => http.Requests.Count(r => r.Path.EndsWith("app_start")) >= 2);
        Assert.Equal(0, Volatile.Read(ref client.AppStartCalls));
        Assert.Empty(remote.SentByClient);

        await client.DisposeAsync();
        Assert.DoesNotContain(http.Requests, r => r.Path.EndsWith("app/end"));
    }

    private static async Task<WebSocket> ParkUntilCancelledAsync(
        TaskCompletionSource<WebSocket> gate, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.Token.Register(() => gate.TrySetCanceled(ct));
        return await gate.Task.ConfigureAwait(false);
    }
}

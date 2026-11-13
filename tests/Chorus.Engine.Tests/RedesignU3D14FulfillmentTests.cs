using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Chorus.ControlServer;
using Chorus.Data;
using Chorus.DataServer;
using Chorus.Platform;
using Chorus.Rpc;
using Google.Protobuf;
using Npgsql;
using Xunit;

namespace Chorus.Engine.Tests;

/// <summary>
/// The platform fulfillment reports (FIX-U3D-14): the end ladder's roundEnd duty translated
/// into the documented platform contracts. The kuaishou adapter rides the documented round
/// stop (app_id and access_token as query parameters, the sorted core fields signed with the
/// secret appended last, success the documented result=1); the douyin adapter carries the
/// mandatory fulfillment ack. Neither platform documents a per-operation record, so both stay
/// honest about re-issue (IdempotentReissue=false: an unobserved outcome escalates, never
/// re-fires) and both stay NotProductionReady until joint debugging verifies the real
/// endpoints. The facts the reports carry — the frozen round result and each mount's launch
/// room code — are read once per pass and ride the task desired json to the adapter.
/// </summary>
public sealed class RedesignU3D14FulfillmentTests
{
    [Fact]
    public void TheKuaishouSignCommitsToTheSortedCoreFieldsWithTheSecretLast()
    {
        var fields = new List<KeyValuePair<string, string>>
        {
            new("module", "round"),
            new("roomCode", "ks-room-9"),
            new("action", "stop"),
        };

        var canonical = KuaishouContract.CanonicalPayload(fields, "app-9", "sekrit");
        Assert.Equal("action=stop&app_id=app-9&module=round&roomCode=ks-room-9&sekrit", canonical);

        // A previous sign never joins its own input.
        var resigned = KuaishouContract.CanonicalPayload(
            [.. fields, new KeyValuePair<string, string>("sign", "stale")], "app-9", "sekrit");
        Assert.Equal(canonical, resigned);

        // The digest is deterministic hex-lowercase of the hash's fixed width. The algorithm
        // itself is the joint-debugging unknown — this pins the shape, not the recipe.
        var digest = KuaishouContract.Sign(fields, "app-9", "sekrit");
        Assert.Equal(digest, KuaishouContract.Sign(fields, "app-9", "sekrit"));
        Assert.Equal(64, digest.Length);
        Assert.Equal(digest.ToLowerInvariant(), digest);
        Assert.True(digest.All(char.IsAsciiHexDigit));
    }

    [Fact]
    public async Task TheKuaishouRoundStopRidesTheDocumentedContract()
    {
        using var platform = new PlatformStub { Answer = PlatformStub.Reply.ResultOne };
        var adapter = new KuaishouFulfillmentAdapter(new Uri(platform.Url), "app-9", "tok-9", "sekrit-9");

        var outcome = await adapter.CallAsync(
            new PlatformTaskCall("end-op-ks", Desired("ks-room-77", "room-1")));

        // The documented success word: HTTP 200 with result=1, carried back as the observed
        // state the task protocol records.
        Assert.True(outcome.Executed);
        Assert.Contains("\"result\":1", outcome.ObservedJson);
        var call = Assert.Single(platform.Calls);
        Assert.Equal("/openapi/developer/live/smallPlay/round", call.Path);
        // Credentials ride the query string, never the body (the documented shape).
        Assert.Equal("app-9", call.Query["app_id"]);
        Assert.Equal("tok-9", call.Query["access_token"]);
        Assert.Equal("round", call.Form["module"]);
        Assert.Equal("stop", call.Form["action"]);
        // The room code is the launch key, the round id the platform room id.
        Assert.Equal("ks-room-77", call.Form["roomCode"]);
        Assert.Equal("room-1", call.Form["roundId"]);
        Assert.Equal("singleGroup", call.Form["roundType"]);
        Assert.True(long.Parse(call.Form["timestamp"], CultureInfo.InvariantCulture) > 0);
        Assert.False(string.IsNullOrEmpty(call.Form["sign"]));
        using var data = JsonDocument.Parse(call.Form["data"]);
        Assert.Equal("room-1", data.RootElement.GetProperty("roomId").GetString());
        Assert.Equal("end-op-ks", data.RootElement.GetProperty("endOperationId").GetString());
        Assert.Equal(3L, data.RootElement.GetProperty("resultVersion").GetInt64());
        Assert.Equal("red", data.RootElement.GetProperty("winner").GetString());
    }

    [Fact]
    public async Task ARefusedKuaishouStopReportsNotExecutedForTheRetry()
    {
        using var platform = new PlatformStub();
        var adapter = new KuaishouFulfillmentAdapter(new Uri(platform.Url), "app-9", "tok-9", "sekrit-9");

        // The documented refusal word: an exchange that succeeded, a stop that did not —
        // the honest not-executed answer the task protocol retries.
        platform.Answer = PlatformStub.Reply.ResultZero;
        var refused = await adapter.CallAsync(
            new PlatformTaskCall("end-op-zero", Desired("ks-room-77", "room-1")));
        Assert.False(refused.Executed);
        Assert.Contains("\"result\":0", refused.ObservedJson);

        // A transport-level fault answers the same way: not executed, retryable.
        platform.Answer = PlatformStub.Reply.ServerError;
        var errored = await adapter.CallAsync(
            new PlatformTaskCall("end-op-500", Desired("ks-room-77", "room-1")));
        Assert.False(errored.Executed);
        Assert.Equal(2, platform.Calls.Count);
    }

    [Fact]
    public async Task ADeclaredNoRoundChangeStaysNotRequired()
    {
        using var platform = new PlatformStub();
        var kuaishou = new KuaishouFulfillmentAdapter(new Uri(platform.Url), "app-9", "tok-9", "sekrit-9");
        var douyin = new DouyinFulfillmentAdapter(new Uri(platform.Url), "app-dy");

        // The ladder's own word — a plain round end's scope covers neither the gameplay bind
        // nor the live — is translated, never guessed into a call; the grounds stay recorded.
        var scoped = JsonSerializer.Serialize(new
        {
            kind = "end-round", roomId = "room-1", endOperationId = "end-op-scoped",
            roomCode = "ks-room-77",
            roundResult = """{"ranking":[],"winner":"draw"}""",
            categories = new Dictionary<string, string>
            {
                ["roundEnd"] = "not-required:the round closed without platform work",
                ["gameplayUnbind"] = "not-required:the round end's scope is the round",
                ["liveEnd"] = "not-required:the round end's scope is the round"
            }
        });
        var kuaishouBasis = await kuaishou.CallAsync(new PlatformTaskCall("end-op-scoped", scoped));
        Assert.True(kuaishouBasis.NotRequired);
        Assert.False(kuaishouBasis.Executed);
        Assert.Contains("declared no platform round change", kuaishouBasis.ObservedJson);
        var douyinBasis = await douyin.CallAsync(new PlatformTaskCall("end-op-scoped", scoped));
        Assert.True(douyinBasis.NotRequired);

        // A desired with no category declaration at all declares nothing — the same refusal
        // to invent a platform call out of silence.
        var undeclared = await kuaishou.CallAsync(new PlatformTaskCall("end-op-plain",
            """{"kind":"end-round","roomId":"room-1"}"""));
        Assert.True(undeclared.NotRequired);
        Assert.Empty(platform.Calls);
    }

    [Fact]
    public async Task AStopWithoutItsFactsIsRefusedVisibly()
    {
        using var platform = new PlatformStub();
        var kuaishou = new KuaishouFulfillmentAdapter(new Uri(platform.Url), "app-9", "tok-9", "sekrit-9");
        var douyin = new DouyinFulfillmentAdapter(new Uri(platform.Url), "app-dy");

        // A declared stop without the room code (a mount whose session row went missing) or
        // without the frozen result is a wiring fault: the adapter refuses visibly so the
        // task retries against repaired facts, and never reports a silent skip.
        var noRoom = JsonSerializer.Serialize(new
        {
            kind = "end-round", roomId = "room-1", endOperationId = "end-op-noroom",
            roomCode = "",
            roundResult = """{"ranking":[],"winner":"red"}""",
            categories = new Dictionary<string, string> { ["roundEnd"] = "apply" }
        });
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            kuaishou.CallAsync(new PlatformTaskCall("end-op-noroom", noRoom)));

        var noResult = JsonSerializer.Serialize(new
        {
            kind = "end-round", roomId = "room-1", endOperationId = "end-op-noresult",
            roomCode = "ks-room-77",
            roundResult = "",
            categories = new Dictionary<string, string> { ["roundEnd"] = "apply" }
        });
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            kuaishou.CallAsync(new PlatformTaskCall("end-op-noresult", noResult)));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            douyin.CallAsync(new PlatformTaskCall("end-op-noresult", noResult)));

        Assert.Empty(platform.Calls);
    }

    [Fact]
    public async Task TheDouyinAckCarriesTheOperationAndItsResult()
    {
        using var platform = new PlatformStub { Answer = PlatformStub.Reply.Ok };
        var adapter = new DouyinFulfillmentAdapter(new Uri(platform.Url), "app-dy");

        var outcome = await adapter.CallAsync(
            new PlatformTaskCall("end-op-dy", Desired("dy-room-5", "room-2")));

        Assert.True(outcome.Executed);
        var call = Assert.Single(platform.Calls);
        Assert.Equal("/fulfillment/ack", call.Path);
        Assert.Equal("app-dy", call.Query["app_id"]);
        using var body = JsonDocument.Parse(call.Body);
        Assert.Equal("end-op-dy", body.RootElement.GetProperty("operationId").GetString());
        Assert.Equal("room-2", body.RootElement.GetProperty("roomId").GetString());
        Assert.Equal(3L, body.RootElement.GetProperty("resultVersion").GetInt64());
        Assert.Equal("red", body.RootElement.GetProperty("winner").GetString());
    }

    [Fact]
    public async Task BothFulfillmentAdaptersStayNotProductionReadyAndHonest()
    {
        using var platform = new PlatformStub();
        var kuaishou = new KuaishouFulfillmentAdapter(new Uri(platform.Url), "app-9", "tok-9", "sekrit-9");
        var douyin = new DouyinFulfillmentAdapter(new Uri(platform.Url), "app-dy");

        // NotProductionReady until the real endpoints are joint-debugged; and neither
        // platform documents a per-operation record, so an unobserved outcome can neither be
        // re-issued (it might double-fire on the platform) nor queried (it escalates).
        Assert.Equal("kuaishou", kuaishou.Platform);
        Assert.False(kuaishou.ProductionReady);
        Assert.False(kuaishou.IdempotentReissue);
        Assert.Equal("douyin", douyin.Platform);
        Assert.False(douyin.ProductionReady);
        Assert.False(douyin.IdempotentReissue);
        await Assert.ThrowsAsync<HttpRequestException>(() => kuaishou.QueryAsync("end-op-ks"));
        await Assert.ThrowsAsync<HttpRequestException>(() => douyin.QueryAsync("end-op-dy"));
    }

    [Fact]
    public async Task TheFulfillmentReaderCarriesTheFrozenResultAndEachRoomCode()
    {
        await using var arrange = await FulfillmentArrange.RoundAsync();
        await arrange.CommitResultAsync(resultVersion: 3,
            """{"ranking":[{"faction":"red","score":50},{"faction":"blue","score":30}],"winner":"red"}""");

        var reader = new RoundFulfillmentReader(arrange.Database.ConnectionString);
        var facts = await reader.ReadAsync("room-14",
            [new EndMountGeneration(arrange.Binding.BindingId, 1, 2)]);

        // The report carries the room's frozen result (jsonb-normalized, so the contract is
        // extractability, not spelling), and the launch room key the live session was opened
        // with — the kuaishou stop's roomCode.
        using var reported = JsonDocument.Parse(facts.ResultJson);
        Assert.Equal("red", reported.RootElement.GetProperty("winner").GetString());
        Assert.Equal("ks-room-42", facts.RoomCodes[arrange.Binding.BindingId]);

        // A binding whose session row is gone carries the empty code: the desired stays
        // buildable, and the adapter's visible refusal is the fault downstream.
        var ghost = await reader.ReadAsync("room-14",
            [new EndMountGeneration("binding-gone", 1, 2)]);
        Assert.Equal("", ghost.RoomCodes["binding-gone"]);

        // The ladder begins on a frozen result: a room with no committed row is a data-plane
        // fault, thrown — never an empty report silently reported as fulfilled.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            reader.ReadAsync("room-never-committed", []));
    }

    [Fact]
    public async Task TheGatewayMountDrivesTheKuaishouStopEndToEnd()
    {
        await using var environment = await RedesignM0HostTests.HostEnvironment.CreateAsync();
        using var platform = new PlatformStub { Answer = PlatformStub.Reply.ResultOne };
        await StartHostsAsync(environment, platform.Url);
        using var face = new TaskFace(environment);
        var tasks = face.Client;
        const string openId = "u3d14-fulfill";
        var binding = await MountAsync(environment, openId);

        // The desired the coordinator records for a round end: the facts ride with it — the
        // launch room code the live session was opened with ("live-" + openId) and the frozen
        // result json — so the gateway's executor translates the documented stop directly.
        var enqueued = await tasks.EnqueueDesiredAsync(new TaskEnqueueRequest
        {
            BindingId = binding,
            OperationId = "end-op-14",
            DesiredJson = Desired("live-" + openId, "room-14", "end-op-14")
        });
        Assert.Equal("Pending", enqueued.Status);

        var completed = await WaitForTaskAsync(tasks, enqueued.TaskId,
            state => state.Status == "Completed", TimeSpan.FromSeconds(60));
        Assert.Equal(1, completed.AttemptCount);
        Assert.Equal("gateway-a", completed.LeaseOwner);
        var call = Assert.Single(platform.Calls);
        Assert.Equal("/openapi/developer/live/smallPlay/round", call.Path);
        Assert.Equal("live-" + openId, call.Form["roomCode"]);
        Assert.Equal("room-14", call.Form["roundId"]);
        Assert.Equal("app-9", call.Query["app_id"]);
        await AssertOutboxAsync(environment, enqueued.TaskId + ":1", "Recorded");
    }

    /// <summary>The end-round desired the coordinator records, mirrored exactly: the facts
    /// ride lowercase, the category declaration travels with the facts.</summary>
    private static string Desired(string roomCode, string roomId,
        string endOperationId = "end-op-ks") =>
        JsonSerializer.Serialize(new
        {
            kind = "end-round",
            tenantId = "tenant-a", gameId = "game-a", roomId,
            endOperationId,
            roundGeneration = 2L, closeRouteVersion = 1L,
            bindingId = "binding-1", bindingGeneration = 1L, bindingRoundGeneration = 2L,
            roomCode,
            roundResult = """{"ranking":[{"faction":"red","score":50}],"winner":"red"}""",
            resultVersion = 3L,
            categories = new Dictionary<string, string>
            {
                ["roundEnd"] = "apply",
                ["gameplayUnbind"] = "not-required:the round end's scope is the round",
                ["liveEnd"] = "not-required:the round end's scope is the round"
            }
        });

    private static async Task<byte[]> StartHostsAsync(RedesignM0HostTests.HostEnvironment environment,
        string platformUrl)
    {
        await environment.StartAsync("Chorus.DataServer", environment.DataPort, "data-server");
        var simulatorKey = RandomNumberGenerator.GetBytes(32);
        var accessKey = RandomNumberGenerator.GetBytes(32);
        await environment.StartAsync("Chorus.StreamerServer", environment.StreamerPort,
            "streamer-server", new Dictionary<string, string>
            {
                ["CHORUS_DATA_PORT"] = environment.DataPort.ToString(CultureInfo.InvariantCulture),
                ["CHORUS_PLATFORM_SIMULATOR"] = "true",
                ["CHORUS_SIMULATOR_SIGNING_KEY"] = Convert.ToHexString(simulatorKey),
                ["CHORUS_ACCESS_CREDENTIAL_KEY"] = Convert.ToHexString(accessKey),
                ["CHORUS_TENANT_APPLICATIONS"] = """{"tenant-a":{"app-1":"game-a"}}"""
            });
        await environment.StartAsync("Chorus.PlatformGateway",
            RedesignM0HostTests.HostEnvironment.FreePort(), "platform-gateway",
            new Dictionary<string, string>
            {
                ["CHORUS_DATA_PORT"] = environment.DataPort.ToString(CultureInfo.InvariantCulture),
                // The fulfillment mount (FIX-U3D-14): the kuaishou adapter speaks the
                // documented contract at the stub; the simulator stays off this gateway.
                ["CHORUS_PLATFORM_ADAPTER"] = "kuaishou",
                ["CHORUS_PLATFORM_ENDPOINT"] = platformUrl,
                ["CHORUS_PLATFORM_APP_ID"] = "app-9",
                ["CHORUS_PLATFORM_ACCESS_TOKEN"] = "tok-9",
                ["CHORUS_PLATFORM_APP_SECRET"] = "sekrit-9",
                ["CHORUS_TASK_RECONCILE_INTERVAL_MS"] = "300",
                ["CHORUS_TASK_LEASE_SECONDS"] = "60",
                ["CHORUS_TASK_ATTEMPT_DEADLINE_SECONDS"] = "70",
                ["CHORUS_TASK_CALL_TIMEOUT_SECONDS"] = "120"
            }, instanceId: "gateway-a");
        return simulatorKey;
    }

    /// <summary>A control-plane face onto the data server's task service; keeps its certificate
    /// and channel alive with the client.</summary>
    private sealed class TaskFace : IDisposable
    {
        public TaskFace(RedesignM0HostTests.HostEnvironment environment)
        {
            _certificate = environment.Certificate("@cluster", "control-server", "control-u3d14");
            _channel = environment.Channel(environment.DataPort, _certificate, "data-server");
            Client = new PlatformTask.PlatformTaskClient(_channel);
        }

        private readonly X509Certificate2 _certificate;
        private readonly Grpc.Net.Client.GrpcChannel _channel;
        public PlatformTask.PlatformTaskClient Client { get; }

        public void Dispose()
        {
            _channel.Dispose();
            _certificate.Dispose();
        }
    }

    private static async Task<string> MountAsync(
        RedesignM0HostTests.HostEnvironment environment, string openId)
    {
        // The task's credential domain is its binding's (review C2): the gateway's kuaishou
        // executor (app-9) only serves (kuaishou, app-9) bindings — the production shape for
        // a kuaishou room. Mounted directly at the stores; the simulator launch session
        // names a different domain and its executor would refuse the task.
        var identityStore = new RedesignIdentityStore(environment.DatabaseConnectionString);
        var bindingStore = new RedesignBindingStore(environment.DatabaseConnectionString);
        var streamer = await identityStore.ResolveIdentityAsync("tenant-a", "kuaishou", "app-9", openId);
        var live = await bindingStore.OpenLiveSessionAsync("tenant-a", streamer.StreamerId, "kuaishou",
            "app-9", "ks-room-" + openId, "live-" + openId);
        var session = await identityStore.LoginAsync(streamer.StreamerId, "gateway-a",
            "connection-" + openId, RandomNumberGenerator.GetBytes(32));
        var mounted = await bindingStore.StartGameplayAsync("tenant-a", live, session.SessionId,
            "game-a", "mount-" + openId);
        return mounted.BindingId;
    }

    private static async Task<TaskStateReply> WaitForTaskAsync(PlatformTask.PlatformTaskClient tasks,
        string taskId, Func<TaskStateReply, bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        TaskStateReply last = new();
        while (DateTime.UtcNow < deadline)
        {
            last = await tasks.GetTaskAsync(new TaskGetRequest { TaskId = taskId });
            if (predicate(last)) return last;
            await Task.Delay(200);
        }
        return predicate(last) ? last : throw new TimeoutException(
            $"The task {taskId} never reached the expected state (last: {last.Status}).");
    }

    private static async Task AssertOutboxAsync(RedesignM0HostTests.HostEnvironment environment,
        string effectId, string status)
    {
        await using var connection = new NpgsqlConnection(environment.DatabaseConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT status FROM infra.outbox WHERE effect_id=@effect", connection);
        command.Parameters.AddWithValue("effect", effectId);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            if (await command.ExecuteScalarAsync() as string == status) return;
            await Task.Delay(200);
        }
        Assert.Fail($"The outbox effect {effectId} never reached {status}.");
    }

    /// <summary>The documented platform faces: the kuaishou round stop (form-encoded, signed)
    /// and the douyin fulfillment ack (json). Every request is recorded — path, query, form,
    /// body — and the answer is the test's choice: the documented success word, the refusal
    /// word, a transport-level fault, or the ack's plain ok.</summary>
    private sealed class PlatformStub : IDisposable
    {
        public enum Reply { ResultOne, ResultZero, ServerError, Ok }

        public sealed record Call(string Path, IReadOnlyDictionary<string, string> Query,
            IReadOnlyDictionary<string, string> Form, string Body);

        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _stop = new();
        private readonly object _gate = new();
        private readonly List<Call> _calls = [];

        public Reply Answer { get; set; } = Reply.ResultOne;
        public string Url { get; }

        public PlatformStub()
        {
            Url = $"http://localhost:{RedesignM0HostTests.HostEnvironment.FreePort()}/";
            _listener.Prefixes.Add(Url);
            _listener.Start();
            _ = RunAsync();
        }

        public IReadOnlyList<Call> Calls { get { lock (_gate) return [.. _calls]; } }

        private async Task RunAsync()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    var context = await _listener.GetContextAsync();
                    _ = Task.Run(() => HandleAsync(context));
                }
            }
            catch (Exception error) when (error is HttpListenerException or ObjectDisposedException)
            {
                // Disposed while waiting: the platform went away with the test.
            }
        }

        private async Task HandleAsync(HttpListenerContext context)
        {
            try
            {
                string body;
                using (var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8))
                    body = await reader.ReadToEndAsync();
                var form = new Dictionary<string, string>(StringComparer.Ordinal);
                if (context.Request.ContentType?.StartsWith("application/x-www-form-urlencoded") == true)
                {
                    foreach (var pair in body.Split('&', StringSplitOptions.RemoveEmptyEntries))
                    {
                        var parts = pair.Split('=', 2);
                        form[Uri.UnescapeDataString(parts[0])] = Uri.UnescapeDataString(
                            parts.Length > 1 ? parts[1].Replace("+", "%20") : "");
                    }
                }
                var query = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var key in context.Request.QueryString.AllKeys)
                    if (key != null) query[key] = context.Request.QueryString[key] ?? "";
                lock (_gate) _calls.Add(new Call(context.Request.Url!.AbsolutePath, query, form, body));

                context.Response.ContentType = "application/json";
                context.Response.StatusCode = Answer == Reply.ServerError ? 500 : 200;
                var payload = Encoding.UTF8.GetBytes(Answer switch
                {
                    Reply.ResultOne => """{"result":1,"error":"","error_desc":"success"}""",
                    Reply.ResultZero => """{"result":0,"error":"2005","error_desc":"room not found"}""",
                    Reply.ServerError => """{"error":"internal"}""",
                    _ => """{"code":0,"desc":"ok"}"""
                });
                await context.Response.OutputStream.WriteAsync(payload);
                context.Response.Close();
            }
            catch (Exception)
            {
                try { context.Response.Abort(); } catch { /* the caller is gone */ }
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
            _listener.Close();
        }
    }

    /// <summary>One mounted binding in one started round on a kuaishou live session — the
    /// minimum the fulfillment reader reports from.</summary>
    private sealed class FulfillmentArrange : IAsyncDisposable
    {
        public TemporaryDatabase Database = null!;
        public RedesignRoomStore RoomStore = null!;
        public RedesignIdentityStore IdentityStore = null!;
        public RedesignBindingStore BindingStore = null!;
        public RedesignControlStore Control = null!;
        public (string BindingId, string StreamerId) Binding { get; set; }

        public static async Task<FulfillmentArrange> RoundAsync()
        {
            var database = await TemporaryDatabase.CreateAsync();
            try
            {
                var arrange = new FulfillmentArrange
                {
                    Database = database,
                    RoomStore = new RedesignRoomStore(database.ConnectionString),
                    IdentityStore = new RedesignIdentityStore(database.ConnectionString),
                    BindingStore = new RedesignBindingStore(database.ConnectionString),
                    Control = new RedesignControlStore(database.ConnectionString)
                };
                var streamer = await arrange.IdentityStore.ResolveIdentityAsync("tenant-a", "kuaishou",
                    "app-ks", "u3d14-reader");
                var live = await arrange.BindingStore.OpenLiveSessionAsync("tenant-a", streamer.StreamerId,
                    "kuaishou", "app-ks", "ks-room-42", "ks-canonical-42");
                var session = await arrange.IdentityStore.LoginAsync(streamer.StreamerId, "gateway-1",
                    "connection-1", RandomNumberGenerator.GetBytes(32));
                var mount = await arrange.BindingStore.StartGameplayAsync("tenant-a", live,
                    session.SessionId, "game-a", "mount-14");
                var claim = await arrange.Control.ClaimOwnershipAsync("tenant-a", "game-a", "room",
                    "room-14", "gs-1", 60);
                await arrange.RoomStore.StartRoundAsync("tenant-a", "game-a", "room-14", "gs-1",
                    claim.Epoch, [new RoundParticipant(mount.BindingId, streamer.StreamerId, "Host")],
                    "1.0.0", "cfg-1", "pkg-1", "start-14");
                arrange.Binding = (mount.BindingId, streamer.StreamerId);
                return arrange;
            }
            catch
            {
                await database.DisposeAsync();
                throw;
            }
        }

        /// <summary>The frozen result row the reader reports from (the frame store's own
        /// column shape).</summary>
        public async Task CommitResultAsync(long resultVersion, string result)
        {
            await using var connection = new NpgsqlConnection(Database.ConnectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(
                """
                INSERT INTO game.result(room_id,result_version,final_frame,plan_hash,result)
                VALUES ('room-14', @version, 900, decode('aa','hex'), @result)
                """, connection);
            command.Parameters.AddWithValue("version", resultVersion);
            command.Parameters.AddWithValue("result", NpgsqlTypes.NpgsqlDbType.Jsonb, result);
            await command.ExecuteNonQueryAsync();
        }

        public async ValueTask DisposeAsync() => await Database.DisposeAsync();
    }

    private sealed class TemporaryDatabase(string source) : IAsyncDisposable
    {
        private readonly string _name = "chorus_u3d14_" + Guid.NewGuid().ToString("N");
        private readonly NpgsqlConnectionStringBuilder _admin = new(source) { Pooling = false };
        public string ConnectionString { get; private set; } = "";

        public static async Task<TemporaryDatabase> CreateAsync()
        {
            var source = Environment.GetEnvironmentVariable("CHORUS_TEST_POSTGRES")
                ?? throw new InvalidOperationException("Set CHORUS_TEST_POSTGRES to a role with CREATEDB.");
            var database = new TemporaryDatabase(source);
            await database.InitializeAsync();
            return database;
        }

        private async Task InitializeAsync()
        {
            await using var admin = new NpgsqlConnection(_admin.ConnectionString);
            await admin.OpenAsync();
            await using (var create = new NpgsqlCommand($"CREATE DATABASE \"{_name}\"", admin))
                await create.ExecuteNonQueryAsync();
            var target = new NpgsqlConnectionStringBuilder(_admin.ConnectionString) { Database = _name };
            ConnectionString = target.ConnectionString;
            await using var connection = new NpgsqlConnection(ConnectionString);
            await connection.OpenAsync();
            await RedesignSchemaMigrator.ApplyAsync(connection);
        }

        public async ValueTask DisposeAsync()
        {
            await using var admin = new NpgsqlConnection(_admin.ConnectionString);
            await admin.OpenAsync();
            await TestDatabaseDrop.DropAsync(admin, _name);
        }
    }
}

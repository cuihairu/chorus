using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Chorus.Redesign.Platform;
using Chorus.Redesign.Rpc;
using Google.Protobuf;
using Grpc.Core;
using Npgsql;
using Xunit;

namespace Chorus.Engine.Tests;

/// <summary>
/// The M3-8 platform-task executor (architecture 6.3): the gateway host drives the lease
/// protocol end to end — the outbox effect is recorded with the attempt, the call goes to the
/// (simulated) external platform, and the observed state lands. A killed executor is replaced
/// by query, never by a blind re-issue: the platform's own record decides executed versus
/// retryable, so an operation executes exactly once however many executors touch it.
/// </summary>
public sealed class RedesignM3PlatformTaskHostTests
{
    [Fact]
    public async Task PlatformTasksExecuteThroughTheGatewayAndCloseTheOutboxEffect()
    {
        await using var environment = await RedesignM0HostTests.HostEnvironment.CreateAsync();
        using var platform = SimulatedPlatform.Start();
        var simulatorKey = await StartHostsAsync(environment, platform.Url);
        using var face = new TaskFace(environment);
        var tasks = face.Client;
        var binding = await MountAsync(environment, simulatorKey, "open-task-1");

        var enqueued = await tasks.EnqueueDesiredAsync(new TaskEnqueueRequest
        {
            BindingId = binding, OperationId = "op-live-1", DesiredJson = """{"kind":"screen-link","state":"active"}"""
        });
        Assert.Equal("Pending", enqueued.Status);

        var completed = await WaitForTaskAsync(tasks, enqueued.TaskId,
            state => state.Status == "Completed", TimeSpan.FromSeconds(30));
        Assert.Equal(1, completed.AttemptCount);
        Assert.Contains("applied", completed.ObservedJson);
        Assert.Equal("gateway-a", completed.LeaseOwner);
        // The outbox effect closed with the attempt: recorded once, for the one call issued.
        Assert.Equal(1, platform.Calls("op-live-1"));
        Assert.True(platform.Executed("op-live-1"));
        await AssertOutboxAsync(environment, enqueued.TaskId + ":1", "Recorded");
    }

    [Fact]
    public async Task AKilledExecutorIsReplacedByQueryWithoutDoubleExecution()
    {
        await using var environment = await RedesignM0HostTests.HostEnvironment.CreateAsync();
        using var platform = SimulatedPlatform.Start();
        var simulatorKey = await StartHostsAsync(environment, platform.Url);
        using var face = new TaskFace(environment);
        var tasks = face.Client;
        var bindingExec = await MountAsync(environment, simulatorKey, "open-task-exec");
        var bindingDrop = await MountAsync(environment, simulatorKey, "open-task-drop");

        // One operation the platform executes after the executor died mid-call; one whose call
        // never landed. Both holders are killed with the calls in flight.
        platform.Hold("op-exec", drop: false);
        platform.Hold("op-drop", drop: true);
        var exec = await tasks.EnqueueDesiredAsync(new TaskEnqueueRequest
        {
            BindingId = bindingExec, OperationId = "op-exec", DesiredJson = """{"kind":"gift","count":1}"""
        });
        var drop = await tasks.EnqueueDesiredAsync(new TaskEnqueueRequest
        {
            BindingId = bindingDrop, OperationId = "op-drop", DesiredJson = """{"kind":"gift","count":2}"""
        });

        // gateway-a picks both up and its calls hang on the platform's hold gates.
        var inFlight = await WaitForAsync(() => Task.FromResult(
            platform.Calls("op-exec") == 1 && platform.Calls("op-drop") == 1), TimeSpan.FromSeconds(45));
        Assert.True(inFlight, "The executor never issued both platform calls.");
        var executing = await tasks.GetTaskAsync(new TaskGetRequest { TaskId = exec.TaskId });
        Assert.Equal("Executing", executing.Status);
        Assert.Equal(1, executing.AttemptCount);
        Assert.Equal("gateway-a", executing.AttemptOwner);

        // The executor dies mid-call. Then the platform's truth lands: op-exec really executed
        // (after the fact), op-drop never reached the platform's record. Only then does a
        // successor start — no second executor existed while the calls were in flight.
        environment.Kill("gateway-a");
        platform.Release("op-exec");
        platform.Release("op-drop");
        await environment.StartAsync("Chorus.Redesign.PlatformGateway",
            RedesignM0HostTests.HostEnvironment.FreePort(), "platform-gateway",
            GatewayEnvironment(environment.DataPort, platform.Url), instanceId: "gateway-b");

        // gateway-b takes over by query: op-exec completes on the official record — the single
        // execution stands, no second call is issued, and the in-flight attempt stays attempt 1.
        var executed = await WaitForTaskAsync(tasks, exec.TaskId,
            state => state.Status == "Completed", TimeSpan.FromSeconds(60));
        Assert.Equal(1, executed.AttemptCount);
        Assert.Equal(3, executed.TaskEpoch);
        Assert.Contains("applied", executed.ObservedJson);
        Assert.Equal(1, platform.Calls("op-exec"));
        await AssertOutboxAsync(environment, exec.TaskId + ":1", "Recorded");

        // op-drop resolves not-executed, becomes retryable, and only then re-issues: the second
        // attempt executes exactly once — two calls, one execution.
        var dropped = await WaitForTaskAsync(tasks, drop.TaskId,
            state => state.Status == "Completed", TimeSpan.FromSeconds(60));
        Assert.Equal(2, dropped.AttemptCount);
        Assert.Equal(4, dropped.TaskEpoch);
        Assert.Contains("applied", dropped.ObservedJson);
        Assert.Equal(2, platform.Calls("op-drop"));
        await AssertOutboxAsync(environment, drop.TaskId + ":1", "NotExecuted");
        await AssertOutboxAsync(environment, drop.TaskId + ":2", "Recorded");
    }

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
        await environment.StartAsync("Chorus.Redesign.PlatformGateway",
            RedesignM0HostTests.HostEnvironment.FreePort(), "platform-gateway",
            GatewayEnvironment(environment.DataPort, platformUrl), instanceId: "gateway-a");
        return simulatorKey;
    }

    private static Dictionary<string, string> GatewayEnvironment(int dataPort, string platformUrl) => new()
    {
        ["CHORUS_DATA_PORT"] = dataPort.ToString(CultureInfo.InvariantCulture),
        ["CHORUS_DEV_PLATFORM_ENDPOINT"] = platformUrl,
        ["CHORUS_TASK_RECONCILE_INTERVAL_MS"] = "300",
        ["CHORUS_TASK_LEASE_SECONDS"] = "4",
        ["CHORUS_TASK_ATTEMPT_DEADLINE_SECONDS"] = "6",
        // The held calls must outlive any scheduler starvation under full-suite load: the kill,
        // not a timeout, is what ends them.
        ["CHORUS_TASK_CALL_TIMEOUT_SECONDS"] = "60"
    };

    /// <summary>A control-plane face onto the data server's task service; keeps its certificate
    /// and channel alive with the client.</summary>
    private sealed class TaskFace : IDisposable
    {
        public TaskFace(RedesignM0HostTests.HostEnvironment environment)
        {
            _certificate = environment.Certificate("@cluster", "control-server", "control-m3-8");
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
        RedesignM0HostTests.HostEnvironment environment, byte[] simulatorKey, string openId)
    {
        var simulator = new DevelopmentPlatformSimulator(simulatorKey);
        var credential = simulator.IssueCredential("app-1", openId, "live-" + openId, TimeSpan.FromMinutes(10));
        using var certificate = environment.Certificate("tenant-a", "streamer-server", "login-" + openId);
        using var channel = environment.Channel(environment.StreamerPort, certificate, "streamer-server");
        var session = new StreamerSession.StreamerSessionClient(channel);
        var login = await session.LoginAsync(new LoginRequest
        {
            Platform = "simulator", AppId = "app-1", LaunchCredential = credential,
            LoginAttemptId = "attempt-" + openId,
            NextResumeSecret = ByteString.CopyFrom(RandomNumberGenerator.GetBytes(32)),
            GatewayInstanceId = "gateway-1", ConnectionId = "connection-" + openId
        });
        var mounted = await session.StartGameplayAsync(new StartGameplayRequest
        {
            AccessCredential = login.AccessCredential, GameId = "game-a", OperationId = "mount-" + openId
        });
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
        Assert.Equal(status, await command.ExecuteScalarAsync());
    }

    private static async Task<bool> WaitForAsync(Func<Task<bool>> probe, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await probe()) return true;
            await Task.Delay(200);
        }
        return await probe();
    }

    /// <summary>
    /// The simulated external platform: an in-process HTTP endpoint that records operations
    /// idempotently by operation id, answers status queries, and can hold a call on a gate so a
    /// test can kill the executor mid-call and then decide, on the official record, whether the
    /// operation executed. It models the one property the task protocol depends on: the record
    /// survives the death of any executor.
    /// </summary>
    private sealed class SimulatedPlatform : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _stop = new();
        private readonly object _gate = new();
        private readonly Dictionary<string, Operation> _operations = new(StringComparer.Ordinal);

        private sealed class Operation
        {
            public int Calls;
            public bool Executed;
            public string? Observed;
            public bool Drop;
            public TaskCompletionSource Release =
                TaskCompletionSourceFactory.CreateCompleted();
        }

        private static class TaskCompletionSourceFactory
        {
            public static TaskCompletionSource CreateCompleted()
            {
                var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                source.SetResult();
                return source;
            }
        }

        public string Url { get; }

        private SimulatedPlatform(int port) => Url = $"http://localhost:{port}/";

        public static SimulatedPlatform Start()
        {
            var platform = new SimulatedPlatform(RedesignM0HostTests.HostEnvironment.FreePort());
            platform._listener.Prefixes.Add(platform.Url);
            platform._listener.Start();
            _ = platform.RunAsync();
            return platform;
        }

        public void Hold(string operationId, bool drop)
        {
            lock (_gate)
            {
                var operation = _operations.TryGetValue(operationId, out var existing)
                    ? existing : _operations[operationId] = new Operation();
                operation.Drop = drop;
                operation.Release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        /// <summary>
        /// Lets held calls through and clears the drop flag: the in-flight call answers to the
        /// (dead) caller per its hold decision, and later calls — a retry attempt — execute.
        /// </summary>
        public void Release(string operationId)
        {
            lock (_gate)
            {
                var operation = _operations[operationId];
                operation.Drop = false;
                operation.Release.TrySetResult();
            }
        }

        public int Calls(string operationId) { lock (_gate) return _operations[operationId].Calls; }

        public bool Executed(string operationId)
        { lock (_gate) return _operations[operationId].Executed; }

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
                var operationId = context.Request.Url!.AbsolutePath == "/status"
                    ? context.Request.QueryString["operationId"]!
                    : (await JsonDocument.ParseAsync(context.Request.InputStream)).RootElement
                        .GetProperty("operationId").GetString()!;
                string response;
                if (context.Request.Url.AbsolutePath == "/status")
                {
                    lock (_gate)
                    {
                        var operation = _operations.TryGetValue(operationId, out var existing)
                            ? existing : _operations[operationId] = new Operation();
                        response = JsonSerializer.Serialize(new
                        {
                            executed = operation.Executed, observedJson = operation.Observed
                        });
                    }
                }
                else
                {
                    // The hold decision is captured when the call arrives: a call that arrived
                    // under a drop-hold fails once released, while later calls execute.
                    TaskCompletionSource gate;
                    bool drop;
                    lock (_gate)
                    {
                        var operation = _operations.TryGetValue(operationId, out var existing)
                            ? existing : _operations[operationId] = new Operation();
                        operation.Calls++;
                        drop = operation.Drop;
                        gate = operation.Release;
                    }
                    await gate.Task;
                    if (drop)
                    {
                        context.Response.StatusCode = 503;
                        context.Response.Close();
                        return;
                    }
                    lock (_gate)
                    {
                        var operation = _operations[operationId];
                        if (!operation.Executed)
                        {
                            operation.Executed = true;
                            operation.Observed = $$"""{"state":"applied","operationId":"{{operationId}}"}""";
                        }
                        response = JsonSerializer.Serialize(new
                        {
                            executed = operation.Executed, observedJson = operation.Observed
                        });
                    }
                }
                context.Response.ContentType = "application/json";
                var payload = System.Text.Encoding.UTF8.GetBytes(response);
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
}

using Chorus.Data;
using Chorus.DataServer;
using Chorus.Rpc;
using Grpc.Core;
using Npgsql;

namespace Chorus.Engine.Tests;

/// <summary>
/// The external platform task face driven as plain method calls. Desired state and lease
/// reassignment belong to the control plane; attempts belong to the gateway holding the lease.
/// Every mutating call is one store transaction, so a reply always reflects a committed task
/// state: the face maps the store's refusals onto their statuses instead of inventing its own.
/// </summary>
public sealed class RedesignPlatformTaskGrpcFaceTests
{
    private const string Owner = "gw-1";
    private const string Binding = "binding-1";
    private const string Desired = """{"operation":"end","platform":"simulator"}""";

    [Fact]
    public async Task ReadyAnswersForTheTaskCallers()
    {
        await using var rig = await TaskRig.CreateAsync();
        var reply = await rig.Service.Ready(new Empty(), rig.Context("control-server", "control-1"));
        Assert.True(reply.Ready);
        Assert.Equal("4", reply.SchemaVersion);
    }

    [Fact]
    public async Task ReadyRefusesAnAbsentCertificate()
    {
        await using var rig = await TaskRig.CreateAsync();
        await rig.Rejects(() => rig.Service.Ready(new Empty(), rig.Certificates.Context(null)),
            StatusCode.Unauthenticated, "Client certificate is absent from request context.");
    }

    [Fact]
    public async Task ReadyRefusesAForeignCertificate()
    {
        await using var rig = await TaskRig.CreateAsync();
        await rig.Rejects(() => rig.Service.Ready(new Empty(),
                rig.Certificates.Context(rig.Certificates.ForeignClusterCertificate())),
            StatusCode.Unauthenticated, "Client certificate identity is invalid.");
    }

    [Fact]
    public async Task ReadyRefusesARoleOutsideTheTaskPlane()
    {
        await using var rig = await TaskRig.CreateAsync();
        await rig.Rejects(() => rig.Service.Ready(new Empty(), rig.Context("data-server", "data-1")),
            StatusCode.PermissionDenied, "Role is not authorized.");
    }

    [Fact]
    public async Task EnqueueDesiredRegistersTheDesiredState()
    {
        await using var rig = await TaskRig.CreateAsync();
        await rig.SeedBindingAsync();
        var reply = await rig.Service.EnqueueDesired(Enqueue(), rig.Context("control-server", "control-1"));
        Assert.Equal(RedesignPlatformTaskStore.Pending, reply.Status);
        Assert.Equal(1, reply.TaskEpoch);
        Assert.Equal(Binding, reply.BindingId);
        Assert.Equal(1, reply.BindingGeneration);
        Assert.False(reply.Duplicate);
    }

    [Fact]
    public async Task EnqueueDesiredAnswersDuplicateForTheSameDesiredState()
    {
        await using var rig = await TaskRig.CreateAsync();
        await rig.SeedBindingAsync();
        var control = rig.Context("control-server", "control-1");
        var first = await rig.Service.EnqueueDesired(Enqueue(), control);
        var replay = await rig.Service.EnqueueDesired(Enqueue(), control);
        Assert.True(replay.Duplicate);
        Assert.Equal(first.TaskId, replay.TaskId);
    }

    [Fact]
    public async Task EnqueueDesiredRefusesAChangedDesiredUnderTheSameOperationId()
    {
        await using var rig = await TaskRig.CreateAsync();
        await rig.SeedBindingAsync();
        var control = rig.Context("control-server", "control-1");
        await rig.Service.EnqueueDesired(Enqueue(), control);
        await rig.Rejects(() => rig.Service.EnqueueDesired(
                Enqueue(desiredJson: """{"operation":"end"}"""), control),
            StatusCode.InvalidArgument, "The operation was retried with a different request.");
    }

    [Fact]
    public async Task EnqueueDesiredRefusesABindingThatDoesNotExist()
    {
        await using var rig = await TaskRig.CreateAsync();
        await rig.Rejects(() => rig.Service.EnqueueDesired(Enqueue(),
                rig.Context("control-server", "control-1")),
            StatusCode.Aborted, "The gameplay binding is not available.");
    }

    [Fact]
    public async Task EnqueueDesiredRefusesARequestMissingItsFields()
    {
        await using var rig = await TaskRig.CreateAsync();
        await rig.SeedBindingAsync();
        var control = rig.Context("control-server", "control-1");
        await rig.Rejects(() => rig.Service.EnqueueDesired(Enqueue(bindingId: ""), control),
            StatusCode.InvalidArgument, "Value is required. (Parameter 'bindingId')");
        await rig.Rejects(() => rig.Service.EnqueueDesired(Enqueue(operationId: ""), control),
            StatusCode.InvalidArgument, "Value is required. (Parameter 'operationId')");
        await rig.Rejects(() => rig.Service.EnqueueDesired(Enqueue(desiredJson: ""), control),
            StatusCode.InvalidArgument, "Value is required. (Parameter 'desiredJson')");
    }

    [Fact]
    public async Task AcquireLeaseMovesTheLeaseToTheGateway()
    {
        await using var rig = await TaskRig.CreateAsync();
        var taskId = await EnqueueAsync(rig);
        var reply = await rig.Service.AcquireLease(Lease(taskId), rig.Context("platform-gateway", "gw-1"));
        Assert.Equal(2, reply.TaskEpoch);
        Assert.Equal(Owner, reply.LeaseOwner);
        Assert.True(reply.LeaseUntilUnixMs > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    [Fact]
    public async Task AcquireLeaseRefusesANonPositiveDuration()
    {
        await using var rig = await TaskRig.CreateAsync();
        var taskId = await EnqueueAsync(rig);
        await rig.Rejects(() => rig.Service.AcquireLease(Lease(taskId, seconds: 0),
                rig.Context("platform-gateway", "gw-1")),
            StatusCode.InvalidArgument, "The lease duration must be positive. (Parameter 'leaseDuration')");
    }

    [Fact]
    public async Task AcquireLeaseRefusesAnUnknownTask()
    {
        await using var rig = await TaskRig.CreateAsync();
        await rig.Rejects(() => rig.Service.AcquireLease(Lease("task-none"),
                rig.Context("platform-gateway", "gw-1")),
            StatusCode.FailedPrecondition, "The platform task does not accept this transition now.");
    }

    [Fact]
    public async Task BeginAttemptRefusesACallerWithoutTheLease()
    {
        await using var rig = await TaskRig.CreateAsync();
        var taskId = await EnqueueAsync(rig);
        await rig.Rejects(() => rig.Service.BeginAttempt(Attempt(taskId),
                rig.Context("platform-gateway", "gw-1")),
            StatusCode.FailedPrecondition, "The platform task does not accept this transition now.");
    }

    [Fact]
    public async Task BeginAttemptRecordsTheAttemptWithTheLeaseHolder()
    {
        await using var rig = await TaskRig.CreateAsync();
        var taskId = await EnqueueAsync(rig);
        await rig.Service.AcquireLease(Lease(taskId), rig.Context("platform-gateway", "gw-1"));
        var reply = await rig.Service.BeginAttempt(Attempt(taskId), rig.Context("platform-gateway", "gw-1"));
        Assert.Equal(RedesignPlatformTaskStore.Executing, reply.Status);
        Assert.Equal(1, reply.AttemptCount);
        Assert.Equal(Owner, reply.AttemptOwner);
    }

    [Fact]
    public async Task CompleteAttemptClosesTheAttemptAsExecuted()
    {
        await using var rig = await TaskRig.CreateAsync();
        var taskId = await EnqueueAsync(rig);
        await rig.Service.AcquireLease(Lease(taskId), rig.Context("platform-gateway", "gw-1"));
        await rig.Service.BeginAttempt(Attempt(taskId), rig.Context("platform-gateway", "gw-1"));
        var reply = await rig.Service.CompleteAttempt(
            Complete("""{"executed":true}""", taskId), rig.Context("platform-gateway", "gw-1"));
        Assert.Equal(RedesignPlatformTaskStore.Completed, reply.Status);
    }

    [Fact]
    public async Task ResolveOutcomeSetstlesAnUnknownAttempt()
    {
        await using var rig = await TaskRig.CreateAsync();
        var taskId = await EnqueueAsync(rig);
        await rig.Service.AcquireLease(Lease(taskId), rig.Context("platform-gateway", "gw-1"));
        await rig.Service.BeginAttempt(Attempt(taskId), rig.Context("platform-gateway", "gw-1"));
        var unknown = await rig.Service.MarkOutcomeUnknown(Error(taskId, "the call timed out"),
            rig.Context("platform-gateway", "gw-1"));
        Assert.Equal(RedesignPlatformTaskStore.OutcomeUnknown, unknown.Status);
        // The resolver answers for the attempt's own owner: the platform says it executed, so
        // the observed state closes the task instead of retrying a call already sent.
        var resolved = await rig.Service.ResolveOutcome(
            Resolve(taskId, executedByPlatform: true, """{"answered":true}"""),
            rig.Context("platform-gateway", "gw-1"));
        Assert.Equal(RedesignPlatformTaskStore.Completed, resolved.Status);
    }

    [Fact]
    public async Task ResolveOutcomeRefusesAReportWithoutItsObservedState()
    {
        await using var rig = await TaskRig.CreateAsync();
        var taskId = await EnqueueAsync(rig);
        await rig.Service.AcquireLease(Lease(taskId), rig.Context("platform-gateway", "gw-1"));
        await rig.Service.BeginAttempt(Attempt(taskId), rig.Context("platform-gateway", "gw-1"));
        await rig.Service.MarkOutcomeUnknown(Error(taskId, "the call timed out"),
            rig.Context("platform-gateway", "gw-1"));
        // A claim that the platform executed needs the platform's own evidence to rest on.
        await rig.Rejects(() => rig.Service.ResolveOutcome(
                Resolve(taskId, executedByPlatform: true, ""), rig.Context("platform-gateway", "gw-1")),
            StatusCode.InvalidArgument, "Value is required. (Parameter 'observedJson')");
    }

    [Fact]
    public async Task MarkRetryFailedSchedulesTheBackoff()
    {
        await using var rig = await TaskRig.CreateAsync();
        var taskId = await EnqueueAsync(rig);
        await rig.Service.AcquireLease(Lease(taskId), rig.Context("platform-gateway", "gw-1"));
        await rig.Service.BeginAttempt(Attempt(taskId), rig.Context("platform-gateway", "gw-1"));
        var reply = await rig.Service.MarkRetryFailed(Retry(taskId, "rate limited"),
            rig.Context("platform-gateway", "gw-1"));
        Assert.Equal(RedesignPlatformTaskStore.Retryable, reply.Status);
        Assert.Equal("rate limited", reply.LastError);
    }

    [Fact]
    public async Task MarkNeedsAttentionStopsTheAutomaticRetries()
    {
        await using var rig = await TaskRig.CreateAsync();
        var taskId = await EnqueueAsync(rig);
        await rig.Service.AcquireLease(Lease(taskId), rig.Context("platform-gateway", "gw-1"));
        await rig.Service.BeginAttempt(Attempt(taskId), rig.Context("platform-gateway", "gw-1"));
        var reply = await rig.Service.MarkNeedsAttention(Stop(taskId, "the platform cannot answer for it"),
            rig.Context("platform-gateway", "gw-1"));
        Assert.Equal(RedesignPlatformTaskStore.NeedsAttention, reply.Status);
    }

    [Fact]
    public async Task RequeueTaskRefusesATaskThatIsNotAttentionBound()
    {
        await using var rig = await TaskRig.CreateAsync();
        var taskId = await EnqueueAsync(rig);
        await rig.Rejects(() => rig.Service.RequeueTask(new TaskRequeueRequest { TaskId = taskId },
                rig.Context("control-server", "control-1")),
            StatusCode.FailedPrecondition, "The platform task does not accept this transition now.");
    }

    [Fact]
    public async Task RequeueTaskDrivesAnAttentionTaskBackToWork()
    {
        await using var rig = await TaskRig.CreateAsync();
        var taskId = await EnqueueAsync(rig);
        await rig.Service.AcquireLease(Lease(taskId), rig.Context("platform-gateway", "gw-1"));
        await rig.Service.BeginAttempt(Attempt(taskId), rig.Context("platform-gateway", "gw-1"));
        await rig.Service.MarkNeedsAttention(Stop(taskId, "the platform cannot answer for it"),
            rig.Context("platform-gateway", "gw-1"));
        var reply = await rig.Service.RequeueTask(new TaskRequeueRequest { TaskId = taskId },
            rig.Context("control-server", "control-1"));
        Assert.Equal(RedesignPlatformTaskStore.Retryable, reply.Status);
        Assert.Equal(3, reply.TaskEpoch);
    }

    [Fact]
    public async Task GetTaskAnswersTheCommittedState()
    {
        await using var rig = await TaskRig.CreateAsync();
        var taskId = await EnqueueAsync(rig);
        var reply = await rig.Service.GetTask(new TaskGetRequest { TaskId = taskId },
            rig.Context("streamer-server", "streamer-1"));
        Assert.Equal(taskId, reply.TaskId);
        Assert.Equal(RedesignPlatformTaskStore.Pending, reply.Status);
    }

    [Fact]
    public async Task GetTaskRefusesAnUnknownTask()
    {
        await using var rig = await TaskRig.CreateAsync();
        await rig.Rejects(() => rig.Service.GetTask(new TaskGetRequest { TaskId = "task-none" },
                rig.Context("streamer-server", "streamer-1")),
            StatusCode.NotFound, "The platform task does not exist.");
    }

    [Fact]
    public async Task ListReadyTasksBoundsThePage()
    {
        await using var rig = await TaskRig.CreateAsync();
        var taskId = await EnqueueAsync(rig);
        // A request beyond the page limit is clamped to the default rather than trusted: the
        // face is the only reader of an untrusted size.
        var reply = await rig.Service.ListReadyTasks(new TaskListRequest { Max = 1000 },
            rig.Context("platform-gateway", "gw-1"));
        Assert.Single(reply.Tasks);
        Assert.Equal(taskId, reply.Tasks[0].TaskId);
    }

    [Fact]
    public async Task GetBarrierAuthorizationAnswersAuthorizedWithoutACredential()
    {
        await using var rig = await TaskRig.CreateAsync();
        var taskId = await EnqueueAsync(rig);
        // A task with no end-operation credential carries no gate: the barrier is scoped to the
        // tasks that bind one, so this answer is authorized with an empty phase.
        var reply = await rig.Service.GetBarrierAuthorization(new TaskBarrierRequest { TaskId = taskId },
            rig.Context("platform-gateway", "gw-1"));
        Assert.True(reply.Authorized);
        Assert.Equal("", reply.Phase);
    }

    [Fact]
    public async Task GetCloseCompletionAnswersIncompleteWhileTasksOutstand()
    {
        await using var rig = await TaskRig.CreateAsync();
        // Two tasks were recorded under the operation's barrier credential, but only one can
        // be enqueued here without a second binding: a count short of the expected keeps the
        // close open — the recording is still in flight, never that the close is complete.
        await rig.SeedBindingAsync();
        var barrier = await rig.Service.EnqueueDesired(Enqueue(barrierEndOperationId: "end-op-1"),
            rig.Context("control-server", "control-1"));
        Assert.Equal("end-op-1", barrier.BarrierEndOperationId);
        var reply = await rig.Service.GetCloseCompletion(new TaskCloseRequest
        {
            EndOperationId = "end-op-1", ExpectedTasks = 2
        }, rig.Context("control-server", "control-1"));
        Assert.False(reply.Complete);
        Assert.Equal(1, reply.Tasks);
        Assert.Equal(1, reply.Outstanding);
    }

    [Fact]
    public async Task GetCloseCompletionRefusesANegativeExpectedCount()
    {
        await using var rig = await TaskRig.CreateAsync();
        // The count is the caller's claim about the ladder it is reading: a negative one is
        // refused at the face as InvalidArgument, before the store's own guard is reached.
        await rig.Rejects(() => rig.Service.GetCloseCompletion(new TaskCloseRequest
            {
                EndOperationId = "end-op-1", ExpectedTasks = -1
            }, rig.Context("control-server", "control-1")),
            StatusCode.InvalidArgument, "The expected task count cannot be negative.");
    }

    [Fact]
    public async Task GetCloseCompletionRefusesACallerThatIsNotTheControlPlane()
    {
        await using var rig = await TaskRig.CreateAsync();
        await rig.Rejects(() => rig.Service.GetCloseCompletion(new TaskCloseRequest
            {
                EndOperationId = "end-op-1", ExpectedTasks = 1
            }, rig.Context("platform-gateway", "gw-1")),
            StatusCode.PermissionDenied, "Role is not authorized.");
    }

    private static async Task<string> EnqueueAsync(TaskRig rig)
    {
        await rig.SeedBindingAsync();
        var reply = await rig.Service.EnqueueDesired(Enqueue(), rig.Context("control-server", "control-1"));
        return reply.TaskId;
    }

    private static TaskEnqueueRequest Enqueue(
        string bindingId = Binding, string operationId = "op-end-1", string desiredJson = Desired,
        string barrierEndOperationId = "") => new()
        {
            BindingId = bindingId, OperationId = operationId, DesiredJson = desiredJson,
            BarrierEndOperationId = barrierEndOperationId
        };

    private static TaskLeaseRequest Lease(string taskId, long seconds = 30) => new()
        { TaskId = taskId, OwnerInstanceId = Owner, LeaseSeconds = seconds };

    private static TaskAttemptRequest Attempt(string taskId) => new()
        { TaskId = taskId, OwnerInstanceId = Owner, DeadlineSeconds = 30 };

    private static TaskCompleteRequest Complete(string observedJson, string taskId) => new()
        { TaskId = taskId, OwnerInstanceId = Owner, ObservedJson = observedJson };

    private static TaskErrorRequest Error(string taskId, string lastError) => new()
        { TaskId = taskId, OwnerInstanceId = Owner, LastError = lastError };

    private static TaskRetryRequest Retry(string taskId, string lastError) => new()
        { TaskId = taskId, OwnerInstanceId = Owner, LastError = lastError, NextAttemptUnixMs = 0 };

    private static TaskStopRequest Stop(string taskId, string lastError) => new()
        { TaskId = taskId, LastError = lastError };

    private static TaskResolveRequest Resolve(string taskId, bool executedByPlatform, string observedJson) => new()
        { TaskId = taskId, OwnerInstanceId = Owner, ExecutedByPlatform = executedByPlatform, ObservedJson = observedJson };

    private sealed record TaskRig(PlatformTaskGrpcService Service,
        TestProcessCertificates Certificates, TemporaryDatabase Database) : IAsyncDisposable
    {
        public static async Task<TaskRig> CreateAsync()
        {
            var database = await TemporaryDatabase.CreateAsync();
            var certificates = new TestProcessCertificates();
            var store = new RedesignPlatformTaskStore(database.ConnectionString);
            return new TaskRig(new PlatformTaskGrpcService(store, certificates.Trusted),
                certificates, database);
        }

        /// <summary>Mounts the binding the task enqueues onto: the store reads its generation
        /// under a share lock, so a face test needs the real row rather than a stub.</summary>
        public async Task SeedBindingAsync(string bindingId = Binding)
        {
            await using var connection = new NpgsqlConnection(Database.ConnectionString);
            await connection.OpenAsync();
            await using var seed = new NpgsqlCommand($"""
                INSERT INTO auth.streamer_state(streamer_id, tenant_id)
                    VALUES ('streamer-1', 'tenant-a');
                INSERT INTO live.session(live_session_id, tenant_id, streamer_id, platform, app_id,
                    platform_room_key, canonical_live_session_key, started_at)
                    VALUES ('session-1', 'tenant-a', 'streamer-1', 'simulator', 'app-1',
                        'key-1', 'simulator:key-1', clock_timestamp());
                INSERT INTO live.binding(binding_id, live_session_id, tenant_id, game_id, generation,
                    state, current_room_id, round_generation)
                    VALUES (@binding, 'session-1', 'tenant-a', 'game-a', 1, 'Ready', NULL, 1)
                """, connection);
            seed.Parameters.AddWithValue("binding", bindingId);
            await seed.ExecuteNonQueryAsync();
        }

        public TestServerCallContext Context(string role, string instance) =>
            Certificates.Context(Certificates.Issue("@cluster", role, instance));

        public async Task Rejects(Func<Task> call, StatusCode code, string? detail)
        {
            var error = await Assert.ThrowsAsync<RpcException>(call);
            Assert.Equal(code, error.Status.StatusCode);
            if (detail is not null) Assert.Equal(detail, error.Status.Detail);
        }

        public async ValueTask DisposeAsync() => await Database.DisposeAsync();
    }

    private sealed class TemporaryDatabase(string source) : IAsyncDisposable
    {
        private readonly string _name = "chorus_task_face_" + Guid.NewGuid().ToString("N");
        private readonly NpgsqlConnectionStringBuilder _admin = new(source) { Pooling = false };
        public string ConnectionString { get; private set; } = "";

        public static async Task<TemporaryDatabase> CreateAsync()
        {
            var source = Environment.GetEnvironmentVariable("CHORUS_TEST_POSTGRES")
                ?? throw new InvalidOperationException("Set CHORUS_TEST_POSTGRES to a PostgreSQL role with CREATEDB.");
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
            // DROP ... WITH (FORCE) collects the database's backends under one lock and
            // checks each pid's privileges after releasing it: a backend that exits in
            // between can have its OS pid reused by another role's connection under a
            // co-tenant fork storm, and the privilege check then lands on that stranger
            // and fails with 42501. The exiting backend is really gone within
            // milliseconds, so a short bounded retry settles it.
            await using var admin = new NpgsqlConnection(_admin.ConnectionString);
            await admin.OpenAsync();
            await TestDatabaseDrop.DropAsync(admin, _name);
        }
    }
}

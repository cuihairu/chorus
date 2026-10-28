using System.Security.Cryptography;
using Chorus.Data.Redesign;
using Chorus.DataServer;
using Chorus.Redesign.Rpc;
using Npgsql;

namespace Chorus.Engine.Tests;

public sealed class RedesignPlatformTaskStoreTests
{
    [Fact]
    public async Task TaskLifecycleIsIdempotentLeaseFencedAndSerializedPerMount()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var identityStore = new RedesignIdentityStore(database.ConnectionString);
        var bindingStore = new RedesignBindingStore(database.ConnectionString);
        var taskStore = new RedesignPlatformTaskStore(database.ConnectionString);
        var binding = await MountAsync(identityStore, bindingStore, "live-key-task");

        // Desired state is idempotent per (binding, operation); a different desired state conflicts.
        var desired = """{"kind":"screen-link","state":"active"}""";
        var task = await taskStore.EnqueueDesiredAsync(binding.BindingId, "op-sub-1", desired);
        Assert.Equal(RedesignPlatformTaskStore.Pending, task.Status);
        Assert.Equal(1, task.TaskEpoch);
        Assert.Equal(0, task.AttemptCount);
        Assert.Equal(1, task.BindingGeneration);
        Assert.False(task.Duplicate);
        var duplicate = await taskStore.EnqueueDesiredAsync(binding.BindingId, "op-sub-1", desired);
        Assert.True(duplicate.Duplicate);
        Assert.Equal(task.TaskId, duplicate.TaskId);
        await Assert.ThrowsAsync<IdempotencyConflictException>(() => taskStore.EnqueueDesiredAsync(
            binding.BindingId, "op-sub-1", """{"kind":"screen-link","state":"paused"}"""));
        await Assert.ThrowsAsync<BindingDeniedException>(() => taskStore.EnqueueDesiredAsync(
            "missing-binding", "op-sub-x", desired));

        // Only the current lease holder may issue the external call.
        await Assert.ThrowsAsync<TaskStateConflictException>(() =>
            taskStore.BeginAttemptAsync(task.TaskId, "gw-a", TimeSpan.FromSeconds(30)));
        var leased = await taskStore.AcquireLeaseAsync(task.TaskId, "gw-a", TimeSpan.FromMinutes(5));
        Assert.Equal(2, leased.TaskEpoch);
        Assert.Equal("gw-a", leased.LeaseOwner);
        await Assert.ThrowsAsync<TaskStateConflictException>(() =>
            taskStore.BeginAttemptAsync(task.TaskId, "gw-b", TimeSpan.FromSeconds(30)));

        var attempt = await taskStore.BeginAttemptAsync(task.TaskId, "gw-a", TimeSpan.FromSeconds(30));
        Assert.Equal(RedesignPlatformTaskStore.Executing, attempt.Status);
        Assert.Equal(1, attempt.AttemptCount);
        Assert.Equal("gw-a", attempt.AttemptOwner);
        Assert.NotNull(attempt.AttemptDeadlineUtc);
        await AssertOutboxAsync(database, task.TaskId + ":1", "Pending");

        // External operations are serial per mount: a second task on the binding must wait.
        var second = await taskStore.EnqueueDesiredAsync(binding.BindingId, "op-sub-2", desired);
        await taskStore.AcquireLeaseAsync(second.TaskId, "gw-a", TimeSpan.FromMinutes(5));
        await Assert.ThrowsAsync<TaskBusyException>(() =>
            taskStore.BeginAttemptAsync(second.TaskId, "gw-a", TimeSpan.FromSeconds(30)));
        Assert.Equal(RedesignPlatformTaskStore.Pending, (await taskStore.GetTaskAsync(second.TaskId))!.Status);

        var completed = await taskStore.CompleteAttemptAsync(task.TaskId, "gw-a",
            """{"state":"active","officialId":"official-1"}""");
        Assert.Equal(RedesignPlatformTaskStore.Completed, completed.Status);
        Assert.Equal("""{"state": "active", "officialId": "official-1"}""", completed.ObservedJson);
        await AssertOutboxAsync(database, task.TaskId + ":1", "Recorded");
        var after = await taskStore.EnqueueDesiredAsync(binding.BindingId, "op-sub-1", desired);
        Assert.True(after.Duplicate);
        Assert.Equal(RedesignPlatformTaskStore.Completed, after.Status);
    }

    [Fact]
    public async Task UnknownOutcomeRequiresResolutionAndNeverRetriesBlindly()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var identityStore = new RedesignIdentityStore(database.ConnectionString);
        var bindingStore = new RedesignBindingStore(database.ConnectionString);
        var taskStore = new RedesignPlatformTaskStore(database.ConnectionString);
        var binding = await MountAsync(identityStore, bindingStore, "live-key-task");

        // A timeout after the call was sent parks the task; nobody may blindly retry it.
        var task = await taskStore.EnqueueDesiredAsync(binding.BindingId, "op-report-1",
            """{"kind":"round-report","round":"r-1"}""");
        await taskStore.AcquireLeaseAsync(task.TaskId, "gw-a", TimeSpan.FromMinutes(5));
        await taskStore.BeginAttemptAsync(task.TaskId, "gw-a", TimeSpan.FromSeconds(30));
        var unknown = await taskStore.MarkOutcomeUnknownAsync(task.TaskId, "gw-a",
            "timeout; request may have reached the platform");
        Assert.Equal(RedesignPlatformTaskStore.OutcomeUnknown, unknown.Status);
        Assert.Equal("timeout; request may have reached the platform", unknown.LastError);
        await AssertOutboxAsync(database, task.TaskId + ":1", "Unknown");
        await Assert.ThrowsAsync<TaskStateConflictException>(() =>
            taskStore.BeginAttemptAsync(task.TaskId, "gw-a", TimeSpan.FromSeconds(30)));

        // The platform is queryable: it did not execute the call, so a fresh attempt is allowed.
        var notExecuted = await taskStore.ResolveOutcomeAsync(task.TaskId, "gw-a",
            executedByPlatform: false, null);
        Assert.Equal(RedesignPlatformTaskStore.Retryable, notExecuted.Status);
        Assert.NotNull(notExecuted.NextAttemptUtc);
        await AssertOutboxAsync(database, task.TaskId + ":1", "NotExecuted");
        var retry = await taskStore.BeginAttemptAsync(task.TaskId, "gw-a", TimeSpan.FromSeconds(30));
        Assert.Equal(2, retry.AttemptCount);
        var done = await taskStore.CompleteAttemptAsync(task.TaskId, "gw-a", """{"state":"reported"}""");
        Assert.Equal(RedesignPlatformTaskStore.Completed, done.Status);
        await AssertOutboxAsync(database, task.TaskId + ":2", "Recorded");

        // Neither idempotent nor queryable: automatic retries stop in NeedsAttention.
        var blind = await taskStore.EnqueueDesiredAsync(binding.BindingId, "op-report-2",
            """{"kind":"gift-refund","order":"o-9"}""");
        await taskStore.AcquireLeaseAsync(blind.TaskId, "gw-a", TimeSpan.FromMinutes(5));
        await taskStore.BeginAttemptAsync(blind.TaskId, "gw-a", TimeSpan.FromSeconds(30));
        await taskStore.MarkOutcomeUnknownAsync(blind.TaskId, "gw-a", "timeout; no idempotency key");
        var stopped = await taskStore.MarkNeedsAttentionAsync(blind.TaskId,
            "platform supports neither idempotency keys nor status queries");
        Assert.Equal(RedesignPlatformTaskStore.NeedsAttention, stopped.Status);
        await Assert.ThrowsAsync<TaskStateConflictException>(() =>
            taskStore.BeginAttemptAsync(blind.TaskId, "gw-a", TimeSpan.FromSeconds(30)));
        await Assert.ThrowsAsync<TaskStateConflictException>(() => taskStore.MarkNeedsAttentionAsync(
            task.TaskId, "already completed"));

        // The process died mid-call: a reassigned executor waits out the old attempt deadline,
        // then verifies the official outcome instead of repeating the call.
        var crashed = await taskStore.EnqueueDesiredAsync(binding.BindingId, "op-report-3",
            """{"kind":"screen-link","state":"active"}""");
        await taskStore.AcquireLeaseAsync(crashed.TaskId, "gw-a", TimeSpan.FromMinutes(5));
        var inFlight = await taskStore.BeginAttemptAsync(crashed.TaskId, "gw-a", TimeSpan.FromTicks(1));
        var reassigned = await taskStore.AcquireLeaseAsync(crashed.TaskId, "gw-b", TimeSpan.FromMinutes(5));
        Assert.Equal(inFlight.TaskEpoch + 1, reassigned.TaskEpoch); // the epoch bumped...
        Assert.Equal(RedesignPlatformTaskStore.Executing, reassigned.Status); // ...but the in-flight attempt stays
        Assert.Equal("gw-a", reassigned.AttemptOwner);
        await Assert.ThrowsAsync<TaskStateConflictException>(() =>
            taskStore.BeginAttemptAsync(crashed.TaskId, "gw-b", TimeSpan.FromSeconds(30)));
        var resolved = await taskStore.ResolveOutcomeAsync(crashed.TaskId, "gw-b",
            executedByPlatform: false, null);
        Assert.Equal(RedesignPlatformTaskStore.Retryable, resolved.Status);
        var takeover = await taskStore.BeginAttemptAsync(crashed.TaskId, "gw-b", TimeSpan.FromSeconds(30));
        Assert.Equal(2, takeover.AttemptCount);
        Assert.Equal(RedesignPlatformTaskStore.Completed,
            (await taskStore.CompleteAttemptAsync(crashed.TaskId, "gw-b", """{"state":"active"}""")).Status);
        // The dead executor's call keeps its place in the outbox history, resolved by the query.
        await AssertOutboxAsync(database, crashed.TaskId + ":1", "NotExecuted");
        await AssertOutboxAsync(database, crashed.TaskId + ":2", "Recorded");
    }

    [Fact]
    public async Task AdminRequeueRevivesNeedsAttentionAtABumpedEpochOnly()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var identityStore = new RedesignIdentityStore(database.ConnectionString);
        var bindingStore = new RedesignBindingStore(database.ConnectionString);
        var taskStore = new RedesignPlatformTaskStore(database.ConnectionString);
        var binding = await MountAsync(identityStore, bindingStore, "live-key-requeue");

        // The blind-call dead end only the control plane may overrule: NeedsAttention → Retryable,
        // due immediately, epoch bumped, lease cleared — then the normal executor path takes over.
        var task = await taskStore.EnqueueDesiredAsync(binding.BindingId, "op-requeue-1",
            """{"kind":"gift-refund","order":"o-42"}""");
        await taskStore.AcquireLeaseAsync(task.TaskId, "gw-a", TimeSpan.FromMinutes(5));
        await taskStore.BeginAttemptAsync(task.TaskId, "gw-a", TimeSpan.FromSeconds(30));
        await taskStore.MarkOutcomeUnknownAsync(task.TaskId, "gw-a", "call timed out");
        var stopped = await taskStore.MarkNeedsAttentionAsync(task.TaskId, "no status query on platform");
        Assert.Equal(RedesignPlatformTaskStore.NeedsAttention, stopped.Status);

        var requeued = await taskStore.RequeueTaskAsync(task.TaskId);
        Assert.Equal(RedesignPlatformTaskStore.Retryable, requeued.Status);
        Assert.Equal(stopped.TaskEpoch + 1, requeued.TaskEpoch);
        // The lease is released so any executor may pick the task up; the dead attempt's owner
        // stays in the history columns, exactly like an executor takeover keeps it.
        Assert.True(string.IsNullOrEmpty(requeued.LeaseOwner));
        Assert.NotNull(requeued.NextAttemptUtc);
        Assert.True(requeued.NextAttemptUtc <= DateTime.UtcNow.AddSeconds(1));
        var ready = await taskStore.ListReadyTasksAsync(16);
        Assert.Contains(ready, item => item.TaskId == task.TaskId);

        await taskStore.AcquireLeaseAsync(task.TaskId, "gw-b", TimeSpan.FromMinutes(5));
        await taskStore.BeginAttemptAsync(task.TaskId, "gw-b", TimeSpan.FromSeconds(30));
        Assert.Equal(RedesignPlatformTaskStore.Completed,
            (await taskStore.CompleteAttemptAsync(task.TaskId, "gw-b", """{"state":"refunded"}""")).Status);

        // Anything but NeedsAttention refuses: completed work never restarts through admin.
        await Assert.ThrowsAsync<TaskStateConflictException>(() => taskStore.RequeueTaskAsync(task.TaskId));
        var queued = await taskStore.EnqueueDesiredAsync(binding.BindingId, "op-requeue-2",
            """{"kind":"poll","round":"r-9"}""");
        await Assert.ThrowsAsync<TaskStateConflictException>(() => taskStore.RequeueTaskAsync(queued.TaskId));
    }

    [Fact]
    public async Task RetryBackoffAndLeaseExpiryGateNewAttempts()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var identityStore = new RedesignIdentityStore(database.ConnectionString);
        var bindingStore = new RedesignBindingStore(database.ConnectionString);
        var taskStore = new RedesignPlatformTaskStore(database.ConnectionString);
        var binding = await MountAsync(identityStore, bindingStore, "live-key-task");

        // A due retry may start immediately; a backoff not yet reached may not.
        var task = await taskStore.EnqueueDesiredAsync(binding.BindingId, "op-poll-1",
            """{"kind":"poll","round":"r-2"}""");
        await taskStore.AcquireLeaseAsync(task.TaskId, "gw-a", TimeSpan.FromMinutes(5));
        await taskStore.BeginAttemptAsync(task.TaskId, "gw-a", TimeSpan.FromSeconds(30));
        var failed = await taskStore.MarkRetryFailedAsync(task.TaskId, "gw-a", "platform 503",
            DateTimeOffset.UtcNow.AddMinutes(10));
        Assert.Equal(RedesignPlatformTaskStore.Retryable, failed.Status);
        Assert.Equal("platform 503", failed.LastError);
        await Assert.ThrowsAsync<TaskStateConflictException>(() =>
            taskStore.BeginAttemptAsync(task.TaskId, "gw-a", TimeSpan.FromSeconds(30)));

        var due = await taskStore.EnqueueDesiredAsync(binding.BindingId, "op-poll-2",
            """{"kind":"poll","round":"r-3"}""");
        await taskStore.AcquireLeaseAsync(due.TaskId, "gw-a", TimeSpan.FromMinutes(5));
        await taskStore.BeginAttemptAsync(due.TaskId, "gw-a", TimeSpan.FromSeconds(30));
        await taskStore.MarkRetryFailedAsync(due.TaskId, "gw-a", "platform 503",
            DateTimeOffset.UtcNow.AddMinutes(-1));
        Assert.Equal(2, (await taskStore.BeginAttemptAsync(due.TaskId, "gw-a", TimeSpan.FromSeconds(30)))
            .AttemptCount);
        await taskStore.CompleteAttemptAsync(due.TaskId, "gw-a", """{"state":"polled"}""");

        // A lease that already expired no longer authorizes new attempts; reassignment restores.
        var expired = await taskStore.EnqueueDesiredAsync(binding.BindingId, "op-poll-3",
            """{"kind":"poll","round":"r-4"}""");
        await taskStore.AcquireLeaseAsync(expired.TaskId, "gw-a", TimeSpan.FromTicks(1));
        await Assert.ThrowsAsync<TaskStateConflictException>(() =>
            taskStore.BeginAttemptAsync(expired.TaskId, "gw-a", TimeSpan.FromSeconds(30)));
        await taskStore.AcquireLeaseAsync(expired.TaskId, "gw-b", TimeSpan.FromMinutes(5));
        Assert.Equal(RedesignPlatformTaskStore.Executing,
            (await taskStore.BeginAttemptAsync(expired.TaskId, "gw-b", TimeSpan.FromSeconds(30))).Status);
    }

    [Fact]
    public async Task ReadyTaskDiscoveryExposesDueWorkAndStaleExecutionsOnly()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var identityStore = new RedesignIdentityStore(database.ConnectionString);
        var bindingStore = new RedesignBindingStore(database.ConnectionString);
        var taskStore = new RedesignPlatformTaskStore(database.ConnectionString);
        var binding1 = await MountAsync(identityStore, bindingStore, "live-key-list-1");
        var binding2 = await MountAsync(identityStore, bindingStore, "live-key-list-2");
        var binding3 = await MountAsync(identityStore, bindingStore, "live-key-list-3");
        var desired = """{"kind":"screen-link","state":"active"}""";

        // Due now: a fresh pending task.
        await taskStore.EnqueueDesiredAsync(binding1.BindingId, "op-list-1", desired);
        // Never listed: completed work.
        var done = await taskStore.EnqueueDesiredAsync(binding1.BindingId, "op-list-2", desired);
        await taskStore.AcquireLeaseAsync(done.TaskId, "gw-a", TimeSpan.FromMinutes(5));
        await taskStore.BeginAttemptAsync(done.TaskId, "gw-a", TimeSpan.FromSeconds(30));
        await taskStore.CompleteAttemptAsync(done.TaskId, "gw-a", """{"state":"active"}""");
        // Not due yet: a retry backing off into the future.
        var backoff = await taskStore.EnqueueDesiredAsync(binding1.BindingId, "op-list-3", desired);
        await taskStore.AcquireLeaseAsync(backoff.TaskId, "gw-a", TimeSpan.FromMinutes(5));
        await taskStore.BeginAttemptAsync(backoff.TaskId, "gw-a", TimeSpan.FromSeconds(30));
        await taskStore.MarkRetryFailedAsync(backoff.TaskId, "gw-a", "platform 503",
            DateTimeOffset.UtcNow.AddMinutes(10));
        // Due again: a retry whose backoff has passed.
        var dueRetry = await taskStore.EnqueueDesiredAsync(binding1.BindingId, "op-list-4", desired);
        // Executing under a live lease: not reassignable.
        var live = await taskStore.EnqueueDesiredAsync(binding2.BindingId, "op-list-5", desired);
        await taskStore.AcquireLeaseAsync(live.TaskId, "gw-a", TimeSpan.FromMinutes(5));
        await taskStore.BeginAttemptAsync(live.TaskId, "gw-a", TimeSpan.FromSeconds(30));
        // Executing under an expired lease: the reassignment candidate.
        var stale = await taskStore.EnqueueDesiredAsync(binding3.BindingId, "op-list-6", desired);
        await taskStore.AcquireLeaseAsync(stale.TaskId, "gw-a", TimeSpan.FromSeconds(30));
        await taskStore.BeginAttemptAsync(stale.TaskId, "gw-a", TimeSpan.FromSeconds(30));
        // OutcomeUnknown: awaiting a verifier with the official record.
        var unknown = await taskStore.EnqueueDesiredAsync(binding1.BindingId, "op-list-7", desired);
        await taskStore.AcquireLeaseAsync(unknown.TaskId, "gw-a", TimeSpan.FromMinutes(5));
        await taskStore.BeginAttemptAsync(unknown.TaskId, "gw-a", TimeSpan.FromSeconds(30));
        await taskStore.MarkOutcomeUnknownAsync(unknown.TaskId, "gw-a", "call timed out");
        // op-list-4 needs its due-retry state: attempt and fail with a passed backoff.
        await taskStore.AcquireLeaseAsync(dueRetry.TaskId, "gw-a", TimeSpan.FromMinutes(5));
        await taskStore.BeginAttemptAsync(dueRetry.TaskId, "gw-a", TimeSpan.FromSeconds(30));
        await taskStore.MarkRetryFailedAsync(dueRetry.TaskId, "gw-a", "platform 503",
            DateTimeOffset.UtcNow.AddMinutes(-1));
        // The stale execution's lease is backdated past expiry.
        await using (var connection = new NpgsqlConnection(database.ConnectionString))
        {
            await connection.OpenAsync();
            await using var backdate = new NpgsqlCommand(
                "UPDATE live.task SET lease_until=clock_timestamp()-interval '1 second' WHERE task_id=@task",
                connection);
            backdate.Parameters.AddWithValue("task", stale.TaskId);
            await backdate.ExecuteNonQueryAsync();
        }

        var ready = await taskStore.ListReadyTasksAsync(16);
        Assert.Equal(["op-list-1", "op-list-4", "op-list-6", "op-list-7"],
            ready.Select(task => task.OperationId).OrderBy(operation => operation, StringComparer.Ordinal).ToArray());
    }

    private static async Task<BindingGrant> MountAsync(
        RedesignIdentityStore identityStore, RedesignBindingStore bindingStore, string canonicalKey)
    {
        var streamer = await identityStore.ResolveIdentityAsync("tenant-a", "simulator", "app-1", "task-open-id");
        var live = await bindingStore.OpenLiveSessionAsync("tenant-a", streamer.StreamerId, "simulator",
            "app-1", "room-1", canonicalKey);
        var session = await identityStore.LoginAsync(streamer.StreamerId, "gateway-1", "connection-1",
            RandomNumberGenerator.GetBytes(32));
        return await bindingStore.StartGameplayAsync("tenant-a", live, session.SessionId, "game-a", "op-mount");
    }

    private static async Task AssertOutboxAsync(TemporaryDatabase database, string effectId, string status)
    {
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT status FROM infra.outbox WHERE effect_id=@effect", connection);
        command.Parameters.AddWithValue("effect", effectId);
        Assert.Equal(status, await command.ExecuteScalarAsync());
    }

    private sealed class TemporaryDatabase(string source) : IAsyncDisposable
    {
        private readonly string _name = "chorus_m1_tasks_" + Guid.NewGuid().ToString("N");
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
            await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{_name}\" WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }
}

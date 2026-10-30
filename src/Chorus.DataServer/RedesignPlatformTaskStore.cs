using System.Text.Json;
using Chorus.Data;
using Chorus.Protocol;
using Chorus.Rpc;
using Npgsql;

namespace Chorus.DataServer;

public sealed record PlatformTaskState(
    string TaskId, string BindingId, long BindingGeneration, long TaskEpoch, string OperationId,
    string Status, int AttemptCount, string DesiredJson, string? ObservedJson, string? LastError,
    DateTimeOffset? NextAttemptUtc, string? LeaseOwner, DateTimeOffset? LeaseUntilUtc,
    string? AttemptOwner, DateTimeOffset? AttemptDeadlineUtc, bool Duplicate);

/// <summary>
/// External platform task coordination (architecture 6.3). A task persists DesiredState,
/// ObservedState, OperationId, TaskEpoch, Attempt, NextAttemptAt and LastError. Executors only
/// run tasks whose lease they currently hold; reassignment bumps the epoch but never clears an
/// in-flight attempt — the new executor must resolve the official outcome first instead of
/// blindly repeating a conflicting call. Every attempt records its external call in the outbox
/// before it may be issued; a timeout parks the task in OutcomeUnknown, and an operation that
/// can neither be made idempotent nor queried stops in NeedsAttention.
/// </summary>
public sealed class RedesignPlatformTaskStore(string connectionString)
{
    public const string Pending = "Pending";
    public const string Executing = "Executing";
    public const string Completed = "Completed";
    public const string Retryable = "Retryable";
    public const string OutcomeUnknown = "OutcomeUnknown";
    public const string NeedsAttention = "NeedsAttention";

    /// <summary>
    /// Records the desired external state for a binding operation. Idempotent per
    /// (binding, operation); the same operation with a different desired state conflicts.
    /// </summary>
    public async Task<PlatformTaskState> EnqueueDesiredAsync(string bindingId, string operationId,
        string desiredJson, CancellationToken cancellationToken = default)
    {
        Require(bindingId, nameof(bindingId));
        Require(operationId, nameof(operationId));
        Require(desiredJson, nameof(desiredJson));
        using (JsonDocument.Parse(desiredJson)) { }
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        long generation;
        await using (var binding = new NpgsqlCommand(
            "SELECT generation FROM live.binding WHERE binding_id=@binding FOR SHARE", connection, transaction))
        {
            binding.Parameters.AddWithValue("binding", bindingId);
            generation = await binding.ExecuteScalarAsync(cancellationToken) as long?
                ?? throw new BindingDeniedException();
        }
        var taskId = Guid.NewGuid().ToString("N");
        await using (var insert = new NpgsqlCommand("""
            INSERT INTO live.task(task_id,binding_id,binding_generation,task_epoch,desired,
                operation_id,status,next_attempt)
            VALUES (@task,@binding,@generation,1,@desired::jsonb,@operation,@status,clock_timestamp())
            ON CONFLICT (binding_id,operation_id) DO NOTHING
            """, connection, transaction))
        {
            insert.Parameters.AddWithValue("task", taskId);
            insert.Parameters.AddWithValue("binding", bindingId);
            insert.Parameters.AddWithValue("generation", generation);
            insert.Parameters.AddWithValue("desired", desiredJson);
            insert.Parameters.AddWithValue("operation", operationId);
            insert.Parameters.AddWithValue("status", Pending);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var existing = new NpgsqlCommand("""
            SELECT task_id,desired = @desired::jsonb FROM live.task
            WHERE binding_id=@binding AND operation_id=@operation
            """, connection, transaction))
        {
            existing.Parameters.AddWithValue("binding", bindingId);
            existing.Parameters.AddWithValue("operation", operationId);
            existing.Parameters.AddWithValue("desired", desiredJson);
            string storedTaskId;
            bool sameDesired;
            await using (var reader = await existing.ExecuteReaderAsync(cancellationToken))
            {
                if (!await reader.ReadAsync(cancellationToken))
                    throw new InvalidOperationException("The platform task disappeared during the transaction.");
                storedTaskId = reader.GetString(0);
                sameDesired = reader.GetBoolean(1);
            }
            if (storedTaskId != taskId)
            {
                if (!sameDesired)
                    throw new IdempotencyConflictException();
                var duplicate = await ReadRequiredAsync(connection, transaction, storedTaskId, cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return ToState(duplicate, Duplicate: true);
            }
        }
        var created = await ReadRequiredAsync(connection, transaction, taskId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return ToState(created, Duplicate: false);
    }

    /// <summary>
    /// Grants or reassigns the execution lease. Reassignment bumps TaskEpoch but leaves an
    /// in-flight attempt untouched: the epoch cannot cancel a call already sent to the platform.
    /// </summary>
    public async Task<PlatformTaskState> AcquireLeaseAsync(string taskId, string ownerInstanceId,
        TimeSpan leaseDuration, CancellationToken cancellationToken = default)
    {
        Require(taskId, nameof(taskId));
        Require(ownerInstanceId, nameof(ownerInstanceId));
        if (leaseDuration <= TimeSpan.Zero)
            throw new ArgumentException("The lease duration must be positive.", nameof(leaseDuration));
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LoadForUpdateAsync(connection, transaction, taskId, cancellationToken);
        await using (var update = new NpgsqlCommand("""
            UPDATE live.task
            SET task_epoch = task_epoch + 1, lease_owner=@owner,
                lease_until = clock_timestamp() + make_interval(secs => @seconds)
            WHERE task_id=@task
            """, connection, transaction))
        {
            update.Parameters.AddWithValue("owner", ownerInstanceId);
            update.Parameters.AddWithValue("seconds", leaseDuration.TotalSeconds);
            update.Parameters.AddWithValue("task", taskId);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }
        var row = await ReadRequiredAsync(connection, transaction, taskId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return ToState(row, Duplicate: false);
    }

    /// <summary>
    /// Starts an external call attempt: the outbox row is written in the same transaction, so
    /// the call may only be issued after the attempt is durably visible. One executing task per
    /// mount is enforced by the partial unique index.
    /// </summary>
    public async Task<PlatformTaskState> BeginAttemptAsync(string taskId, string ownerInstanceId,
        TimeSpan attemptDeadline, CancellationToken cancellationToken = default)
    {
        Require(taskId, nameof(taskId));
        Require(ownerInstanceId, nameof(ownerInstanceId));
        if (attemptDeadline <= TimeSpan.Zero)
            throw new ArgumentException("The attempt deadline must be positive.", nameof(attemptDeadline));
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var row = await LoadForUpdateAsync(connection, transaction, taskId, cancellationToken);
        if (row.LeaseOwner != ownerInstanceId || row.LeaseUntil is null ||
            row.LeaseUntil.Value <= DateTimeOffset.UtcNow)
            throw new TaskStateConflictException();
        if (row.Status is not (Pending or Retryable))
            throw new TaskStateConflictException();
        if (row.NextAttempt is not null && row.NextAttempt.Value > DateTimeOffset.UtcNow)
            throw new TaskStateConflictException();
        var attempt = row.AttemptCount + 1;
        try
        {
            await using (var update = new NpgsqlCommand("""
                UPDATE live.task
                SET attempt_count=@attempt, status=@executing, attempt_owner=@owner,
                    attempt_deadline = clock_timestamp() + make_interval(secs => @seconds),
                    last_error=NULL, next_attempt=NULL
                WHERE task_id=@task
                """, connection, transaction))
            {
                update.Parameters.AddWithValue("attempt", attempt);
                update.Parameters.AddWithValue("executing", Executing);
                update.Parameters.AddWithValue("owner", ownerInstanceId);
                update.Parameters.AddWithValue("seconds", attemptDeadline.TotalSeconds);
                update.Parameters.AddWithValue("task", taskId);
                await update.ExecuteNonQueryAsync(cancellationToken);
            }
        }
        catch (PostgresException error) when (error.SqlState == "23505")
        {
            throw new TaskBusyException();
        }
        var effectId = $"{taskId}:{attempt}";
        await using (var outbox = new NpgsqlCommand("""
            INSERT INTO infra.outbox(effect_id,kind,payload,status,attempt_count)
            VALUES (@effect,'platform.call',@payload::jsonb,'Pending',@attempt)
            ON CONFLICT (effect_id) DO NOTHING
            """, connection, transaction))
        {
            outbox.Parameters.AddWithValue("effect", effectId);
            outbox.Parameters.AddWithValue("payload", JsonSerializer.Serialize(new
            {
                TaskId = taskId, row.OperationId, Attempt = attempt, row.Desired
            }));
            outbox.Parameters.AddWithValue("attempt", attempt);
            await outbox.ExecuteNonQueryAsync(cancellationToken);
        }
        var updated = await ReadRequiredAsync(connection, transaction, taskId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return ToState(updated, Duplicate: false);
    }

    /// <summary>Records the official observed state after a successful external call.</summary>
    public Task<PlatformTaskState> CompleteAttemptAsync(string taskId, string ownerInstanceId,
        string observedJson, CancellationToken cancellationToken = default)
    {
        Require(observedJson, nameof(observedJson));
        using (JsonDocument.Parse(observedJson)) { }
        return FinishAttemptAsync(taskId, ownerInstanceId, Completed, observedJson, null,
            "Recorded", cancellationToken);
    }

    /// <summary>
    /// A call timed out (or the process died mid-call). Without a queryable official outcome
    /// the task stays here; blind retries of possibly-conflicting calls are not allowed.
    /// </summary>
    public Task<PlatformTaskState> MarkOutcomeUnknownAsync(string taskId, string ownerInstanceId,
        string lastError, CancellationToken cancellationToken = default) =>
        FinishAttemptAsync(taskId, ownerInstanceId, OutcomeUnknown, null, lastError,
            "Unknown", cancellationToken);

    /// <summary>A failed attempt that may be retried after NextAttemptAt.</summary>
    public Task<PlatformTaskState> MarkRetryFailedAsync(string taskId, string ownerInstanceId,
        string lastError, DateTimeOffset nextAttemptUtc, CancellationToken cancellationToken = default) =>
        FinishAttemptAsync(taskId, ownerInstanceId, Retryable, null, lastError,
            "Failed", cancellationToken, nextAttemptUtc);

    private async Task<PlatformTaskState> FinishAttemptAsync(string taskId, string ownerInstanceId,
        string status, string? observedJson, string? lastError, string outboxStatus,
        CancellationToken cancellationToken = default, DateTimeOffset? nextAttempt = null)
    {
        Require(taskId, nameof(taskId));
        Require(ownerInstanceId, nameof(ownerInstanceId));
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var row = await LoadForUpdateAsync(connection, transaction, taskId, cancellationToken);
        if (row.Status != Executing || row.AttemptOwner != ownerInstanceId)
            throw new TaskStateConflictException();
        await using (var update = new NpgsqlCommand("""
            UPDATE live.task
            SET status=@status, observed=COALESCE(@observed::jsonb, observed),
                last_error=@error, next_attempt=@next
            WHERE task_id=@task
            """, connection, transaction))
        {
            update.Parameters.AddWithValue("status", status);
            if (observedJson is null) update.Parameters.AddWithValue("observed", DBNull.Value);
            else update.Parameters.AddWithValue("observed", observedJson);
            if (lastError is null) update.Parameters.AddWithValue("error", DBNull.Value);
            else update.Parameters.AddWithValue("error", lastError);
            if (nextAttempt is null) update.Parameters.AddWithValue("next", DBNull.Value);
            else update.Parameters.AddWithValue("next", nextAttempt.Value.UtcDateTime);
            update.Parameters.AddWithValue("task", taskId);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var outbox = new NpgsqlCommand(
            "UPDATE infra.outbox SET status=@status WHERE effect_id=@effect", connection, transaction))
        {
            outbox.Parameters.AddWithValue("status", outboxStatus);
            outbox.Parameters.AddWithValue("effect", $"{taskId}:{row.AttemptCount}");
            await outbox.ExecuteNonQueryAsync(cancellationToken);
        }
        var updated = await ReadRequiredAsync(connection, transaction, taskId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return ToState(updated, Duplicate: false);
    }

    /// <summary>
    /// Stops automatic retries: the operation can neither be made idempotent nor queried, so a
    /// human decides. Terminal unless re-driven manually.
    /// </summary>
    public async Task<PlatformTaskState> MarkNeedsAttentionAsync(string taskId, string lastError,
        CancellationToken cancellationToken = default)
    {
        Require(taskId, nameof(taskId));
        Require(lastError, nameof(lastError));
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var row = await LoadForUpdateAsync(connection, transaction, taskId, cancellationToken);
        if (row.Status == Completed)
            throw new TaskStateConflictException();
        await using (var update = new NpgsqlCommand("""
            UPDATE live.task SET status=@status, last_error=@error WHERE task_id=@task
            """, connection, transaction))
        {
            update.Parameters.AddWithValue("status", NeedsAttention);
            update.Parameters.AddWithValue("error", lastError);
            update.Parameters.AddWithValue("task", taskId);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var outbox = new NpgsqlCommand(
            "UPDATE infra.outbox SET status='Stopped' WHERE effect_id=@effect", connection, transaction))
        {
            outbox.Parameters.AddWithValue("effect", $"{taskId}:{row.AttemptCount}");
            await outbox.ExecuteNonQueryAsync(cancellationToken);
        }
        var updated = await ReadRequiredAsync(connection, transaction, taskId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return ToState(updated, Duplicate: false);
    }

    /// <summary>
    /// The admin's controlled retry: only a NeedsAttention task may requeue, and only to
    /// Retryable due immediately at a bumped epoch — the decision to try again is auditable on
    /// the control plane, and the executors pick the task up through the normal lease path.
    /// </summary>
    public async Task<PlatformTaskState> RequeueTaskAsync(string taskId, CancellationToken cancellationToken = default)
    {
        Require(taskId, nameof(taskId));
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var row = await LoadForUpdateAsync(connection, transaction, taskId, cancellationToken);
        if (row.Status != NeedsAttention)
            throw new TaskStateConflictException();
        await using (var update = new NpgsqlCommand("""
            UPDATE live.task SET status=@status, task_epoch=task_epoch+1,
                next_attempt=clock_timestamp(), lease_until=NULL, lease_owner=NULL
            WHERE task_id=@task
            """, connection, transaction))
        {
            update.Parameters.AddWithValue("status", Retryable);
            update.Parameters.AddWithValue("task", taskId);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }
        var updated = await ReadRequiredAsync(connection, transaction, taskId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return ToState(updated, Duplicate: false);
    }

    /// <summary>
    /// Resolves an unknown outcome from the platform's queryable state: executed tasks complete
    /// with the official observation, not-executed tasks become retryable immediately. Callable
    /// on OutcomeUnknown by anyone who then queried the platform, and on a still-Executing task
    /// by the reassigned lease holder once the old attempt's deadline has passed — the epoch
    /// cannot cancel an in-flight call, but its deadline bounds the wait before verification.
    /// </summary>
    public async Task<PlatformTaskState> ResolveOutcomeAsync(string taskId, string ownerInstanceId,
        bool executedByPlatform, string? observedJson, CancellationToken cancellationToken = default)
    {
        Require(taskId, nameof(taskId));
        Require(ownerInstanceId, nameof(ownerInstanceId));
        if (executedByPlatform) Require(observedJson, nameof(observedJson));
        if (observedJson is not null) using (JsonDocument.Parse(observedJson)) { }
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var row = await LoadForUpdateAsync(connection, transaction, taskId, cancellationToken);
        var deadlinePassed = row.AttemptDeadline is null || row.AttemptDeadline <= DateTimeOffset.UtcNow;
        var mayResolve = row.Status == OutcomeUnknown
                ? row.AttemptOwner == ownerInstanceId || row.LeaseOwner == ownerInstanceId
                : row.Status == Executing && row.LeaseOwner == ownerInstanceId && deadlinePassed;
        if (!mayResolve)
            throw new TaskStateConflictException();
        await using (var update = new NpgsqlCommand("""
            UPDATE live.task
            SET status=@status, observed=COALESCE(@observed::jsonb, observed),
                next_attempt=@next
            WHERE task_id=@task
            """, connection, transaction))
        {
            update.Parameters.AddWithValue("status", executedByPlatform ? Completed : Retryable);
            if (observedJson is null || !executedByPlatform)
                update.Parameters.AddWithValue("observed", DBNull.Value);
            else
                update.Parameters.AddWithValue("observed", observedJson);
            if (executedByPlatform) update.Parameters.AddWithValue("next", DBNull.Value);
            else update.Parameters.AddWithValue("next", DateTimeOffset.UtcNow.UtcDateTime);
            update.Parameters.AddWithValue("task", taskId);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var outbox = new NpgsqlCommand(
            "UPDATE infra.outbox SET status=@status WHERE effect_id=@effect", connection, transaction))
        {
            outbox.Parameters.AddWithValue("status", executedByPlatform ? "Recorded" : "NotExecuted");
            outbox.Parameters.AddWithValue("effect", $"{taskId}:{row.AttemptCount}");
            await outbox.ExecuteNonQueryAsync(cancellationToken);
        }
        var updated = await ReadRequiredAsync(connection, transaction, taskId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return ToState(updated, Duplicate: false);
    }

    public async Task<PlatformTaskState?> GetTaskAsync(string taskId, CancellationToken cancellationToken = default)
    {
        Require(taskId, nameof(taskId));
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        var row = await ReadAsync(connection, null, taskId, cancellationToken);
        return row is null ? null : ToState(row, Duplicate: false);
    }

    /// <summary>
    /// Executor discovery: due work the lease protocol hands out — Pending, Retryable past its
    /// backoff, OutcomeUnknown awaiting a verifier — plus tasks left Executing by a holder whose
    /// lease expired (the reassignment candidates: a new lease holder resolves the official
    /// outcome after the old attempt's deadline instead of re-issuing the call). Completed and
    /// NeedsAttention never appear; a live lease never appears.
    /// </summary>
    public async Task<IReadOnlyList<PlatformTaskState>> ListReadyTasksAsync(int max,
        CancellationToken cancellationToken = default)
    {
        if (max <= 0) throw new ArgumentOutOfRangeException(nameof(max));
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT task_id,binding_id,binding_generation,task_epoch,operation_id,status,attempt_count,
                desired::text,observed::text,last_error,next_attempt,lease_owner,lease_until,
                attempt_owner,attempt_deadline
            FROM live.task
            WHERE (status IN ('Pending','Retryable','OutcomeUnknown')
                    AND (next_attempt IS NULL OR next_attempt<=clock_timestamp()))
               OR (status='Executing' AND lease_until IS NOT NULL AND lease_until<clock_timestamp())
            ORDER BY next_attempt NULLS FIRST, task_id
            LIMIT @max
            """, connection);
        command.Parameters.AddWithValue("max", max);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var tasks = new List<PlatformTaskState>();
        while (await reader.ReadAsync(cancellationToken))
            tasks.Add(ToState(ReadRow(reader), Duplicate: false));
        return tasks;
    }

    private sealed record TaskRow(
        string TaskId, string BindingId, long BindingGeneration, long TaskEpoch, string OperationId,
        string Status, int AttemptCount, string Desired, string? Observed, string? LastError,
        DateTimeOffset? NextAttempt, string? LeaseOwner, DateTimeOffset? LeaseUntil,
        string? AttemptOwner, DateTimeOffset? AttemptDeadline);

    private static async Task<TaskRow> LoadForUpdateAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, string taskId, CancellationToken cancellationToken)
        => await ReadAsync(connection, transaction, taskId, cancellationToken, forUpdate: true)
            ?? throw new TaskStateConflictException();

    private static async Task<TaskRow> ReadRequiredAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, string taskId, CancellationToken cancellationToken)
        => await ReadAsync(connection, transaction, taskId, cancellationToken)
            ?? throw new InvalidOperationException("The platform task disappeared during the transaction.");

    private static async Task<TaskRow?> ReadAsync(NpgsqlConnection connection,
        NpgsqlTransaction? transaction, string taskId, CancellationToken cancellationToken,
        bool forUpdate = false)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT task_id,binding_id,binding_generation,task_epoch,operation_id,status,attempt_count,
                desired::text,observed::text,last_error,next_attempt,lease_owner,lease_until,
                attempt_owner,attempt_deadline
            FROM live.task WHERE task_id=@task{(forUpdate ? " FOR UPDATE" : "")}
            """, connection, transaction);
        command.Parameters.AddWithValue("task", taskId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return ReadRow(reader);
    }

    private static TaskRow ReadRow(NpgsqlDataReader reader) => new(
        reader.GetString(0), reader.GetString(1), reader.GetInt64(2), reader.GetInt64(3),
        reader.GetString(4), reader.GetString(5), reader.GetInt32(6), reader.GetString(7),
        reader.IsDBNull(8) ? null : reader.GetString(8),
        reader.IsDBNull(9) ? null : reader.GetString(9),
        reader.IsDBNull(10) ? null : new DateTimeOffset(reader.GetDateTime(10), TimeSpan.Zero),
        reader.IsDBNull(11) ? null : reader.GetString(11),
        reader.IsDBNull(12) ? null : new DateTimeOffset(reader.GetDateTime(12), TimeSpan.Zero),
        reader.IsDBNull(13) ? null : reader.GetString(13),
        reader.IsDBNull(14) ? null : new DateTimeOffset(reader.GetDateTime(14), TimeSpan.Zero));

    private static PlatformTaskState ToState(TaskRow row, bool Duplicate) => new(
        row.TaskId, row.BindingId, row.BindingGeneration, row.TaskEpoch, row.OperationId, row.Status,
        row.AttemptCount, row.Desired, row.Observed, row.LastError, row.NextAttempt, row.LeaseOwner,
        row.LeaseUntil, row.AttemptOwner, row.AttemptDeadline, Duplicate);

    private static void Require(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("Value is required.", name);
    }
}

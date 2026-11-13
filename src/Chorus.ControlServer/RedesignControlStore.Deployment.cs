using System.Text.Json;
using Npgsql;

namespace Chorus.ControlServer;

public sealed record DeploymentPlan(string SlotId, string NodeId, string Role, string DesiredState,
    string DesiredVersion, string PackageHash, long PlanGeneration, string? CommandId,
    DateTime? CommandExpiresAtUtc);

public sealed partial class RedesignControlStore
{
    /// <summary>
    /// Applies a deployment plan (architecture 10.2). The plan generation is monotonic: an
    /// older plan never overwrites a newer one, so a re-delivered or reordered command is a
    /// visible no-op for the store, and the agent never sees the plan history rewind.
    /// </summary>
    public async Task<DeploymentPlan> ApplyPlanAsync(string nodeId, string slotId, string role,
        string desiredState, string desiredVersion, string packageHash, long planGeneration,
        string? commandId, DateTime? commandExpiresAtUtc, CancellationToken cancellationToken = default)
    {
        if (planGeneration <= 0) throw new ArgumentOutOfRangeException(nameof(planGeneration));
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            INSERT INTO control.deployment(slot_id,node_id,role,desired_version,plan_generation,
                desired_state,package_hash,command_id,command_expires_at)
            VALUES (@slot,@node,@role,@version,@generation,@state,@package,@command,@expires)
            ON CONFLICT(slot_id) DO UPDATE
            SET node_id=excluded.node_id,role=excluded.role,desired_version=excluded.desired_version,
                plan_generation=excluded.plan_generation,desired_state=excluded.desired_state,
                package_hash=excluded.package_hash,command_id=excluded.command_id,
                command_expires_at=excluded.command_expires_at,updated_at=clock_timestamp()
            WHERE excluded.plan_generation>=control.deployment.plan_generation
            RETURNING slot_id,node_id,role,desired_state,desired_version,package_hash,
                plan_generation,command_id,command_expires_at
            """, connection);
        command.Parameters.AddWithValue("slot", slotId);
        command.Parameters.AddWithValue("node", nodeId);
        command.Parameters.AddWithValue("role", role);
        command.Parameters.AddWithValue("version", desiredVersion);
        command.Parameters.AddWithValue("generation", planGeneration);
        command.Parameters.AddWithValue("state", desiredState);
        command.Parameters.AddWithValue("package", packageHash);
        if (commandId is null) command.Parameters.AddWithValue("command", DBNull.Value);
        else command.Parameters.AddWithValue("command", commandId);
        if (commandExpiresAtUtc is null) command.Parameters.AddWithValue("expires", DBNull.Value);
        else command.Parameters.AddWithValue("expires", commandExpiresAtUtc);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            // The generation guard refused the write: return the plan that won.
            await reader.DisposeAsync();
            await using var current = new NpgsqlCommand("""
                SELECT slot_id,node_id,role,desired_state,desired_version,package_hash,
                    plan_generation,command_id,command_expires_at
                FROM control.deployment WHERE slot_id=@slot
                """, connection);
            current.Parameters.AddWithValue("slot", slotId);
            await using var currentReader = await current.ExecuteReaderAsync(cancellationToken);
            if (!await currentReader.ReadAsync(cancellationToken))
                throw new InvalidOperationException("The deployment plan disappeared mid-apply.");
            return ReadPlan(currentReader);
        }
        var applied = ReadPlan(reader);
        await reader.DisposeAsync();
        // FIX-AGENT-02: Draining is not just a deployment wish — it lands on the live
        // process row the moment the plan wins, so the scheduler stops assigning new
        // rooms before the agent's next report (up to 5s later) would carry it. The
        // per-instance ratchet (FIX-SCHED-02) keeps the instance pinned from there:
        // its own reports cannot un-drain it, and a restart under a Running plan is a
        // new incarnation with a fresh row.
        if (applied.DesiredState == "Draining")
        {
            await using var pin = new NpgsqlCommand(
                "UPDATE control.process SET admission='Draining' WHERE slot_id=@slot", connection);
            pin.Parameters.AddWithValue("slot", slotId);
            await pin.ExecuteNonQueryAsync(cancellationToken);
        }
        return applied;
    }

    /// <summary>The plans of one node — the WatchPlans face. Unapplied bootstrap rows surface
    /// with their seed values ('bootstrap' version, generation 1).</summary>
    public async Task<IReadOnlyList<DeploymentPlan>> ListPlansAsync(string nodeId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        var plans = new List<DeploymentPlan>();
        await using (var command = new NpgsqlCommand("""
            SELECT slot_id,node_id,role,desired_state,desired_version,package_hash,
                plan_generation,command_id,command_expires_at
            FROM control.deployment WHERE node_id=@node ORDER BY slot_id
            """, connection))
        {
            command.Parameters.AddWithValue("node", nodeId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                plans.Add(ReadPlan(reader));
        }
        return plans;
    }

    /// <summary>The game slots whose desired state is still Draining (FIX-AGENT-02): the
    /// drain-confirmation sweep watches these and escalates each one to Drained once its
    /// host holds no more active rooms.</summary>
    public async Task<IReadOnlyList<DeploymentPlan>> FindDrainingGameSlotsAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        var plans = new List<DeploymentPlan>();
        await using (var command = new NpgsqlCommand("""
            SELECT slot_id,node_id,role,desired_state,desired_version,package_hash,
                plan_generation,command_id,command_expires_at
            FROM control.deployment WHERE desired_state='Draining' AND role='game-server'
            ORDER BY slot_id
            """, connection))
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                plans.Add(ReadPlan(reader));
        }
        return plans;
    }

    /// <summary>Resolves the process instance currently reported for a slot — the live
    /// incarnation a drain sweep must count rooms against (FIX-AGENT-02). A stopped slot
    /// has no live row and drains trivially.</summary>
    public async Task<string?> FindSlotProcessInstanceAsync(string slotId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT process_instance_id FROM control.process
            WHERE slot_id=@slot AND lifecycle IN ('Running','Draining')
            ORDER BY observed_at DESC, started_at DESC LIMIT 1
            """, connection);
        command.Parameters.AddWithValue("slot", slotId);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is string instanceId ? instanceId : null;
    }

    /// <summary>Counts the Active room leases a process still holds (FIX-AGENT-02): a
    /// draining game host is drained exactly when this is zero. Expired leases do not
    /// count — an owner whose lease ran out is already takeable.</summary>
    public async Task<int> CountActiveRoomOwnershipsAsync(string processInstanceId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT count(*) FROM control.ownership
            WHERE kind='room' AND status='Active'
              AND owner_process_instance_id=@owner
              AND lease_until>clock_timestamp()
            """, connection);
        command.Parameters.AddWithValue("owner", processInstanceId);
        var count = await command.ExecuteScalarAsync(cancellationToken);
        return count is long value ? (int)value : 0;
    }

    /// <summary>Escalates a draining game slot to Drained (FIX-AGENT-02). The command id is
    /// fresh so the agent acts on the escalation even when it already applied the Draining
    /// command, and the expiry is cleared for the same reason — the original expiry named
    /// the original command. The generation guard keeps the sweep from rolling back an
    /// operator's newer plan; a re-delivered Draining command can momentarily rewind the
    /// row, and the sweep simply escalates it again.</summary>
    public async Task<bool> EscalateDrainedAsync(string slotId, long planGeneration, string commandId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            UPDATE control.deployment
            SET desired_state='Drained',command_id=@command,command_expires_at=NULL,
                updated_at=clock_timestamp()
            WHERE slot_id=@slot AND desired_state='Draining' AND plan_generation=@generation
            """, connection);
        command.Parameters.AddWithValue("slot", slotId);
        command.Parameters.AddWithValue("generation", planGeneration);
        command.Parameters.AddWithValue("command", commandId);
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    private static DeploymentPlan ReadPlan(NpgsqlDataReader reader) => new(
        reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
        reader.GetString(4), reader.GetString(5), reader.GetInt64(6),
        reader.IsDBNull(7) ? null : reader.GetString(7),
        reader.IsDBNull(8) ? null : reader.GetDateTime(8));
}

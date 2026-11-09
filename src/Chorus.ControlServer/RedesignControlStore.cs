using System.Text.Json;
using Npgsql;

namespace Chorus.ControlServer;

/// <summary>The end sequence's phase ladder (architecture 6.4, FIX-END-01): one row walks
/// these in order — the begin records the frozen result, every later step records its own
/// completion, and Done is terminal. The names are the persisted contract the later end
/// items drive; the row is progress, never authority.</summary>
public static class EndOperationPhases
{
    /// <summary>Step 1: the final frame and result are committed and the actor stopped its
    /// battle ticks — the begin's own evidence.</summary>
    public const string ResultFrozen = "ResultFrozen";
    /// <summary>Step 2: the closing input window — accepted inputs reach their terminal
    /// disposition without changing the outcome.</summary>
    public const string ClosingInput = "ClosingInput";
    /// <summary>Step 3: the stop-delivery command is persisted and issued to the gateways.
    /// </summary>
    public const string StopDeliveryIssued = "StopDeliveryIssued";
    /// <summary>Step 4: every authorized gateway confirmed DeliveryStopped.</summary>
    public const string DeliveryStopped = "DeliveryStopped";
    /// <summary>Step 5: the platform end task is authorized against the closed barrier.
    /// </summary>
    public const string PlatformTaskAuthorized = "PlatformTaskAuthorized";
    /// <summary>Step 6: the platform close answered success or a grounded NotRequired.
    /// </summary>
    public const string PlatformClosed = "PlatformClosed";
    /// <summary>Step 7: the finalize released the participations, pointers and capacity.
    /// </summary>
    public const string Released = "Released";
    /// <summary>Step 8: the room's runtime entities are destroyed.</summary>
    public const string Destroyed = "Destroyed";
    /// <summary>The sequence is complete; the operation is history.</summary>
    public const string Done = "Done";

    private static readonly string[] Ladder =
    [
        ResultFrozen, ClosingInput, StopDeliveryIssued, DeliveryStopped,
        PlatformTaskAuthorized, PlatformClosed, Released, Destroyed, Done
    ];

    /// <summary>The phase a begin starts at.</summary>
    public const string First = ResultFrozen;

    public static bool IsNext(string from, string to)
    {
        var fromIndex = Array.IndexOf(Ladder, from);
        return fromIndex >= 0 && fromIndex + 1 < Ladder.Length && Ladder[fromIndex + 1] == to;
    }
}

public sealed record NodeStatus(string AgentInstanceId, long ReportSequence);
public sealed record ProcessStatus(bool Accepted, string Readiness, string EntityRecovery);
public sealed record OwnerStatus(string OwnerInstanceId, long Epoch, DateTime LeaseUntilUtc, string Status);

/// <summary>The outcome of a GM room-epoch bump (FIX-GM-02): the old and new epoch, the room's
/// owner (unchanged by the bump) and the ownership status it was parked at — Recovering. A
/// replay of the same operation returns the recorded outcome with <see cref="Duplicate"/> set.</summary>
public sealed record EpochBumpOutcome(long OldEpoch, long NewEpoch, string OwnerInstanceId,
    string OwnershipStatus, bool Duplicate);

/// <summary>A room's epoch as the authority reads it (FIX-GM-06): the ownership row is the
/// fence, so its epoch and owner are authoritative; a room with no ownership row yet falls
/// back to the room row's own epoch (the two must agree once both exist, FIX-GM-02).</summary>
public sealed record RoomEpochStatus(bool Found, long Epoch, string? OwnerInstanceId, string? Status);

/// <summary>A persistent room-recovery operation (FIX-TAKE-04): the transfer's target and
/// epoch, the open phase, its deadline, the committed progress and the last error.</summary>
public sealed record RoomRecoveryOperation(
    string TenantId, string GameId, string OperationId, string RoomId, string FromInstance,
    string ToInstance, long TargetEpoch, string Phase, DateTime DeadlineAt, long LastTick,
    string? LastError);
public sealed record RoomRoute(string RoomId, string TenantId, string GameId, string OwnerInstanceId,
    long Epoch, string? Endpoint, string Status);
public sealed record StreamerRoute(string StreamerId, string BindingId, string RoomId, string State,
    string? OwnerInstanceId, long Epoch, string Platform, string CanonicalLiveSessionKey,
    long BindingGeneration, long RoundGeneration, string TenantId);
public sealed record InstanceRoute(string ProcessInstanceId, string Role, string NodeId, string SlotId,
    string? Endpoint, string Readiness, string Admission);
public sealed record RoutingSnapshot(long Version, IReadOnlyList<RoomRoute> Rooms,
    IReadOnlyList<StreamerRoute> Streamers, IReadOnlyList<InstanceRoute> Instances);
public sealed record DeploymentPlan(string SlotId, string NodeId, string Role, string DesiredState,
    string DesiredVersion, string PackageHash, long PlanGeneration, string? CommandId,
    DateTime? CommandExpiresAtUtc);

/// <summary>One participant mount's generations as the end operation recorded them at
/// begin (FIX-END-01): the binding's own generation and the round generation it entered
/// this round with.</summary>
public sealed record EndMountGeneration(string BindingId, long Generation, long RoundGeneration);

/// <summary>A persistent end operation (FIX-END-01, architecture 6.4): the business id
/// every end step binds itself to, the room's round generation and the mount generations
/// that entered it, the phase the sequence stands at, and the last error. The row is
/// progress, never authority — ownership, routing and the settlement stores stay the
/// fences. The release block (FIX-SETTLE-05) records the evidence reason the data plane
/// refused, when the continuous block began and the cumulative dwell across all block
/// episodes — the total freezes on the release and the reason clears.</summary>
public sealed record EndOperation(
    string TenantId, string GameId, string EndOperationId, string RoomId, long RoundGeneration,
    IReadOnlyList<EndMountGeneration> BindingGenerations, long FinalTick, long ResultVersion,
    string Phase, string? LastError, DateTime CreatedAt, DateTime UpdatedAt,
    string? BlockedReason, DateTime? BlockedSince, long BlockedTotalMs);

/// <summary>Thrown when a begin names a generation the mounts have moved past (or no
/// longer point at the room): a late request records nothing and cannot touch the newer
/// round the mounts entered.</summary>
public sealed class StaleRoundGenerationException(string message) : Exception(message);

/// <summary>A room whose result froze without its end operation (FIX-END-04): the persisted
/// receipt the discovery sweep turns into a begin — the evidence is the room's own last
/// tick and state version, not a caller's claim.</summary>
public sealed record UnreportedFinishingRoom(
    string TenantId, string GameId, string RoomId, long LastTick, long StateVersion);

/// <summary>One tenant-and-game domain the settlement executor walks (FIX-END-11). A room
/// with unapplied grants is always Finishing — the terminator refuses an incomplete
/// manifest — so the Finishing rooms name every domain the settle queue can hold.</summary>
public sealed record SettleDomain(string TenantId, string GameId);

/// <summary>
/// The settlement executor's persistent task state for one room (FIX-SETTLE-03): the claim
/// lease, the bounded retry position and the queryable failure reason. A row exists only
/// while the room has unapplied grants — a completed settlement deletes it.
/// </summary>
public sealed record SettleTaskState(
    string RoomId, string Status, string? OwnerInstanceId, DateTime? LeaseUntil,
    int AttemptCount, string? LastError, DateTime? NextAttempt);

/// <summary>Thrown when a second, different business id wants to begin the end of a
/// round that already has its one operation.</summary>
public sealed class EndOperationConflictException(string message) : Exception(message);

/// <summary>The persisted stop-delivery command (FIX-END-05, architecture 6.4 step 3): the
/// room, the round generation the begin derived from the mounts, and the CloseRouteVersion
/// computed from the operation's own recorded facts at issue. The version is frozen at issue —
/// a repeated delivery of the recorded command always carries the same version, so a replay
/// can never close a newer round.</summary>
public sealed record StopDeliveryCommand(
    string TenantId, string GameId, string EndOperationId, string RoomId, long RoundGeneration,
    long CloseRouteVersion, DateTime IssuedAt);

/// <summary>One gateway in the stop-delivery responder set (FIX-END-05): the process instance,
/// the endpoint the command is pushed at (null when the gateway confirmed routing but reported
/// no reachable endpoint), and the per-responder state the later steps drive — an offline but
/// authorized gateway stays Pending until it answers. The step-4 answer (FIX-END-07) freezes
/// the responder's own facts: the cutoff watermark its push leg observed and the informational
/// drain counts of its confirmation pass.</summary>
public sealed record StopDeliveryResponder(
    string ProcessInstanceId, string? Endpoint, string State, DateTime? AnsweredAt,
    long CutoffSequence = 0, long DrainedOutOfRound = 0, long DrainedRouted = 0,
    bool HoldsAuthorization = false);

/// <summary>The step-4 ack's answer (FIX-END-07): whether this call recorded the responder's
/// first confirmation, and the version it recorded against — a repeat at the recorded version
/// answers Duplicate with the first answer's facts standing unchanged.</summary>
public sealed record DeliveryStopAck(bool Recorded, bool Duplicate, long RecordedCloseRouteVersion);

/// <summary>The mount's route source as the control plane recorded it (FIX-END-02): the
/// binding the mounts entered, its generation, and the platform identity the login
/// verified — the identity is derived from the live session join, never asserted by the
/// reporter.</summary>
public sealed record RouteSourceRecord(
    string TenantId, string GameId, string StreamerId, string BindingId, string Platform,
    string CanonicalLiveSessionKey, long BindingGeneration, string RoomId,
    DateTime RegisteredAt, DateTime UpdatedAt);

/// <summary>Thrown when a route-source report names a mount that does not exist, belongs
/// to another streamer, tenant or game, or carries no platform-verified live session: an
/// unverified platform identity establishes no route.</summary>
public sealed class RouteSourceUnverifiedException(string message) : Exception(message);

/// <summary>Thrown when a route-source report names a generation older than the recorded
/// row (or not the mount's current one): late evidence never overwrites the newer
/// registration.</summary>
public sealed class StaleBindingGenerationException(string message) : Exception(message);

/// <summary>A round.started outbox event claimed for dispatch (FIX-LINK-04): the effect the
/// dispatcher must deliver and the commit facts the payload carries — the room, its scope, the
/// ownership epoch, and the participants with the round generation their bindings advanced to
/// (the payload's Generation; the mount generation and the owning instance are resolved per binding
/// at dispatch time).</summary>
public sealed record RoundStartedEvent(
    string EffectId, string RoomId, string TenantId, string GameId, long Epoch,
    IReadOnlyList<RoundStartedParticipant> Participants);

/// <summary>One participant of a claimed round.started event, as the payload carries it: the
/// binding id and the round generation the binding's round pointer advanced to.</summary>
public sealed record RoundStartedParticipant(string BindingId, long RoundGeneration);

/// <summary>The per-participant dispatch target (FIX-LINK-04): the streamer, the mount it is
/// on (binding + mount generation), the room round generation, and the owning StreamerServer
/// instance and its endpoint when the process reports resolved them. Endpoint is null while
/// the owner is unknown — the dispatcher retries rather than guess an address.</summary>
public sealed record ParticipationTarget(
    string StreamerId, string BindingId, long BindingGeneration, long RoundGeneration,
    string? OwnerInstanceId, string? Endpoint);

/// <summary>M0 control writes. Database time and row locks decide ownership and report order.</summary>
public sealed class RedesignControlStore(string connectionString)
{
    public async Task<NodeStatus> RegisterNodeAsync(string nodeId, string agentInstanceId, string capabilitiesJson,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            INSERT INTO control.node(node_id,agent_instance_id,capabilities,report_sequence)
            VALUES (@node,@agent,@capabilities::jsonb,0)
            ON CONFLICT(node_id) DO UPDATE
            SET agent_instance_id=excluded.agent_instance_id,
                capabilities=excluded.capabilities,
                report_sequence=CASE WHEN control.node.agent_instance_id=excluded.agent_instance_id
                    THEN control.node.report_sequence ELSE 0 END,
                observed_at=clock_timestamp()
            RETURNING agent_instance_id,report_sequence
            """, connection);
        command.Parameters.AddWithValue("node", nodeId);
        command.Parameters.AddWithValue("agent", agentInstanceId);
        command.Parameters.AddWithValue("capabilities", capabilitiesJson);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) throw new InvalidOperationException("Node registration returned no row.");
        return new NodeStatus(reader.GetString(0), reader.GetInt64(1));
    }

    public async Task<ProcessStatus> ReportProcessAsync(
        string nodeId, string agentInstanceId, string slotId, string role, string processInstanceId,
        long reportSequence, string lifecycle, string readiness, string health, string admission,
        string entityRecovery, DateTime startedAtUtc, string? endpoint = null, bool selfReport = false,
        CancellationToken cancellationToken = default)
    {
        if (reportSequence <= 0) throw new ArgumentOutOfRangeException(nameof(reportSequence));
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        // A supervising agent speaks for its node; a self-reporting process speaks for itself —
        // the node row lock still serializes both against a concurrent agent takeover.
        await using (var node = new NpgsqlCommand("""
            SELECT agent_instance_id FROM control.node WHERE node_id=@node FOR UPDATE
            """, connection, transaction))
        {
            node.Parameters.AddWithValue("node", nodeId);
            if (await node.ExecuteScalarAsync(cancellationToken) is not string current
                || (!selfReport && current != agentInstanceId))
                return new ProcessStatus(false, "NotReady", "StaleAgent");
        }

        await using (var seed = new NpgsqlCommand("""
            INSERT INTO control.deployment(slot_id,node_id,role,desired_version,plan_generation,desired_state,package_hash)
            VALUES (@slot,@node,@role,'bootstrap',1,'Running','bootstrap')
            ON CONFLICT(slot_id) DO NOTHING
            """, connection, transaction))
        {
            seed.Parameters.AddWithValue("slot", slotId);
            seed.Parameters.AddWithValue("node", nodeId);
            seed.Parameters.AddWithValue("role", role);
            await seed.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var slot = new NpgsqlCommand(
            "SELECT node_id,role FROM control.deployment WHERE slot_id=@slot FOR UPDATE", connection, transaction))
        {
            slot.Parameters.AddWithValue("slot", slotId);
            await using var slotReader = await slot.ExecuteReaderAsync(cancellationToken);
            if (!await slotReader.ReadAsync(cancellationToken) || slotReader.GetString(0) != nodeId ||
                slotReader.GetString(1) != role)
                return new ProcessStatus(false, "NotReady", "SlotMismatch");
        }

        // The upsert orders incarnations, not just sequences (FIX-SCHED-01): a restarted
        // process under the same instance id starts its sequence over with a later
        // started_at, and the newer incarnation takes the row over — its endpoint replaces
        // the dead one's. A replay from an older incarnation, or a stale sequence inside the
        // same one, is rejected; both leave the row exactly as the live process left it.
        await using var command = new NpgsqlCommand("""
            INSERT INTO control.process(process_instance_id,slot_id,node_id,agent_instance_id,report_sequence,
                lifecycle,readiness,health,admission,entity_recovery,started_at,endpoint)
            VALUES (@instance,@slot,@node,@agent,@sequence,@lifecycle,@readiness,@health,@admission,@recovery,
                @started,@endpoint)
            ON CONFLICT(process_instance_id) DO UPDATE
            SET report_sequence=excluded.report_sequence,started_at=excluded.started_at,
                lifecycle=excluded.lifecycle,
                readiness=excluded.readiness,health=excluded.health,
                -- Draining is the admin's word and it is one way (FIX-SCHED-02): a drained
                -- instance that keeps reporting cannot un-drain itself — returning to
                -- service is an admin act, never the host's optimism.
                admission=CASE WHEN control.process.admission='Draining'
                    THEN 'Draining' ELSE excluded.admission END,
                entity_recovery=excluded.entity_recovery,endpoint=excluded.endpoint,observed_at=clock_timestamp()
            WHERE control.process.agent_instance_id=excluded.agent_instance_id
              AND (control.process.started_at<excluded.started_at
                   OR (control.process.started_at=excluded.started_at
                       AND control.process.report_sequence<excluded.report_sequence))
            RETURNING readiness,entity_recovery
            """, connection, transaction);
        command.Parameters.AddWithValue("instance", processInstanceId);
        command.Parameters.AddWithValue("slot", slotId);
        command.Parameters.AddWithValue("node", nodeId);
        command.Parameters.AddWithValue("agent", agentInstanceId);
        command.Parameters.AddWithValue("sequence", reportSequence);
        command.Parameters.AddWithValue("lifecycle", lifecycle);
        command.Parameters.AddWithValue("readiness", readiness);
        command.Parameters.AddWithValue("health", health);
        command.Parameters.AddWithValue("admission", admission);
        command.Parameters.AddWithValue("recovery", entityRecovery);
        command.Parameters.AddWithValue("started", startedAtUtc);
        if (endpoint is null) command.Parameters.AddWithValue("endpoint", DBNull.Value);
        else command.Parameters.AddWithValue("endpoint", endpoint);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var accepted = await reader.ReadAsync(cancellationToken);
        var result = accepted
            ? new ProcessStatus(true, reader.GetString(0), reader.GetString(1))
            : new ProcessStatus(false, "NotReady", "StaleReport");
        await reader.DisposeAsync();
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    /// <summary>The live game-host directory feed (FIX-SCHED-01): game-server processes with
    /// a reported endpoint, alive (Running, or Draining — a drained host keeps serving its
    /// existing rooms, so the control plane keeps its address; only Exited is gone) and
    /// observed within the freshness window. This is the valid process report the
    /// ClusterDirectory is maintained from — staleness, not a config file, decides who
    /// stays reachable. Admission and readiness ride along (FIX-SCHED-02): the directory
    /// keeps every reachable host and its scheduler filters on these, so a drained or full
    /// host stops taking NEW rooms without becoming unreachable for the ones it holds.</summary>
    public async Task<IReadOnlyList<GameHostReport>> ListGameHostReportsAsync(
        int freshnessSeconds, CancellationToken cancellationToken = default)
    {
        if (freshnessSeconds <= 0) throw new ArgumentOutOfRangeException(nameof(freshnessSeconds));
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT p.process_instance_id,p.endpoint,p.observed_at,p.readiness,p.admission
            FROM control.process p
            JOIN control.deployment d ON d.slot_id=p.slot_id
            WHERE d.role='game-server'
              AND p.endpoint IS NOT NULL
              AND p.lifecycle IN ('Running','Draining')
              AND p.observed_at>=clock_timestamp()-@fresh*interval '1 second'
            ORDER BY p.process_instance_id
            """, connection);
        command.Parameters.AddWithValue("fresh", freshnessSeconds);
        var reports = new List<GameHostReport>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            reports.Add(new GameHostReport(reader.GetString(0), reader.GetString(1),
                reader.GetDateTime(2), reader.GetString(3), reader.GetString(4)));
        return reports;
    }

    public async Task<OwnerStatus> ClaimOwnershipAsync(
        string tenantId, string gameId, string kind, string entityId,
        string ownerInstanceId, int leaseSeconds, CancellationToken cancellationToken = default,
        string? sessionId = null, long sessionVersion = 0)
    {
        if (leaseSeconds is <= 0 or > 60) throw new ArgumentOutOfRangeException(nameof(leaseSeconds));
        // A streamer claim is versioned (FIX-AUTH-02): it must name the acting session and is
        // admitted only while that session is the streamer's current one, so a displaced
        // holder's late claim cannot flip routing back once a rotation moved the session on.
        if (StringComparer.Ordinal.Equals(kind, "streamer"))
        {
            await using var streamerConnection = new NpgsqlConnection(connectionString);
            await streamerConnection.OpenAsync(cancellationToken);
            await using var transaction =
                await streamerConnection.BeginTransactionAsync(cancellationToken);
            await using (var state = new NpgsqlCommand("""
                SELECT tenant_id,current_session_id,current_session_version
                FROM auth.streamer_state WHERE streamer_id=@entity FOR UPDATE
                """, streamerConnection, transaction))
            {
                state.Parameters.AddWithValue("entity", entityId);
                await using var stateReader = await state.ExecuteReaderAsync(cancellationToken);
                if (!await stateReader.ReadAsync(cancellationToken))
                    throw new InvalidOperationException(
                        $"The streamer {entityId} has no session state; a routing claim names the current session.");
                if (!StringComparer.Ordinal.Equals(stateReader.GetString(0), tenantId))
                    throw new InvalidOperationException(
                        "The streamer belongs to another tenant; the routing claim is out of scope.");
                var currentSessionId = stateReader.IsDBNull(1) ? null : stateReader.GetString(1);
                var currentSessionVersion = stateReader.GetInt64(2);
                if (string.IsNullOrEmpty(sessionId) || sessionVersion <= 0)
                    throw new InvalidOperationException(
                        "A streamer routing claim names the acting session and its version; this claim carries none.");
                if (!StringComparer.Ordinal.Equals(sessionId, currentSessionId) ||
                    sessionVersion != currentSessionVersion)
                    throw new InvalidOperationException(
                        $"The claim names session {sessionId} (version {sessionVersion}), which is not the streamer's current session (version {currentSessionVersion}); routing stays with the current holder.");
            }
            await using var claimCommand = new NpgsqlCommand("""
                INSERT INTO control.ownership(kind,entity_id,tenant_id,game_id,owner_process_instance_id,
                    epoch,lease_until,status,version)
                VALUES (@kind,@entity,@tenant,@game,@owner,1,
                    clock_timestamp()+@lease*interval '1 second','Active',1)
                ON CONFLICT(kind,entity_id) DO UPDATE
                SET owner_process_instance_id=excluded.owner_process_instance_id,
                    epoch=CASE WHEN control.ownership.owner_process_instance_id=excluded.owner_process_instance_id
                        AND control.ownership.lease_until>clock_timestamp()
                        THEN control.ownership.epoch ELSE control.ownership.epoch+1 END,
                    lease_until=excluded.lease_until,status='Active',version=control.ownership.version+1
                WHERE control.ownership.tenant_id=excluded.tenant_id
                  AND control.ownership.game_id=excluded.game_id
                RETURNING owner_process_instance_id,epoch,lease_until,status
                """, streamerConnection, transaction);
            BindOwner(claimCommand, tenantId, gameId, kind, entityId);
            claimCommand.Parameters.AddWithValue("owner", ownerInstanceId);
            claimCommand.Parameters.AddWithValue("lease", leaseSeconds);
            OwnerStatus status;
            await using (var claimReader = await claimCommand.ExecuteReaderAsync(cancellationToken))
            {
                if (!await claimReader.ReadAsync(cancellationToken))
                    throw new InvalidOperationException("Ownership scope conflicts with an existing entity.");
                status = new OwnerStatus(claimReader.GetString(0), claimReader.GetInt64(1),
                    claimReader.GetDateTime(2), claimReader.GetString(3));
            }
            await transaction.CommitAsync(cancellationToken);
            return status;
        }
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            INSERT INTO control.ownership(kind,entity_id,tenant_id,game_id,owner_process_instance_id,
                epoch,lease_until,status,version)
            VALUES (@kind,@entity,@tenant,@game,@owner,1,
                clock_timestamp()+@lease*interval '1 second','Active',1)
            ON CONFLICT(kind,entity_id) DO UPDATE
            SET owner_process_instance_id=excluded.owner_process_instance_id,
                epoch=CASE WHEN control.ownership.owner_process_instance_id=excluded.owner_process_instance_id
                    AND control.ownership.lease_until>clock_timestamp()
                    THEN control.ownership.epoch ELSE control.ownership.epoch+1 END,
                lease_until=excluded.lease_until,status='Active',version=control.ownership.version+1
            WHERE control.ownership.tenant_id=excluded.tenant_id
              AND control.ownership.game_id=excluded.game_id
            RETURNING owner_process_instance_id,epoch,lease_until,status
            """, connection);
        BindOwner(command, tenantId, gameId, kind, entityId);
        command.Parameters.AddWithValue("owner", ownerInstanceId);
        command.Parameters.AddWithValue("lease", leaseSeconds);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw new InvalidOperationException("Ownership scope conflicts with an existing entity.");
        return new OwnerStatus(reader.GetString(0), reader.GetInt64(1), reader.GetDateTime(2), reader.GetString(3));
    }

    /// <summary>Reads a room's epoch for the GM entry (FIX-GM-06). The ownership row is the
    /// authoritative fence (epoch, owner, status); a room with no ownership row yet falls back
    /// to the room row's epoch. An unknown room reads as not found.</summary>
    public async Task<RoomEpochStatus> GetRoomEpochAsync(string tenantId, string gameId, string roomId,
        CancellationToken cancellationToken = default)
    {
        RoomEpochRule.ValidateIdentifiers(tenantId, gameId, roomId);
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var ownership = new NpgsqlCommand("""
            SELECT owner_process_instance_id,epoch,status FROM control.ownership
            WHERE tenant_id=@tenant AND game_id=@game AND kind='room' AND entity_id=@room
            """, connection);
        ownership.Parameters.AddWithValue("tenant", tenantId);
        ownership.Parameters.AddWithValue("game", gameId);
        ownership.Parameters.AddWithValue("room", roomId);
        await using (var reader = await ownership.ExecuteReaderAsync(cancellationToken))
        {
            if (await reader.ReadAsync(cancellationToken))
                return new RoomEpochStatus(true, reader.GetInt64(1), reader.GetString(0), reader.GetString(2));
        }
        await using var room = new NpgsqlCommand("""
            SELECT epoch,status FROM game.room
            WHERE tenant_id=@tenant AND game_id=@game AND room_id=@room
            """, connection);
        room.Parameters.AddWithValue("tenant", tenantId);
        room.Parameters.AddWithValue("game", gameId);
        room.Parameters.AddWithValue("room", roomId);
        await using var roomReader = await room.ExecuteReaderAsync(cancellationToken);
        return await roomReader.ReadAsync(cancellationToken)
            ? new RoomEpochStatus(true, roomReader.GetInt64(0), null, roomReader.GetString(1))
            : new RoomEpochStatus(false, 0, null, null);
    }

    /// <summary>The route-change publication state of one room (FIX-GM-03): the storage commit
    /// is a separate fact from the platform gateway's confirmation of the routing version that
    /// carries the room's new epoch. "Synced" only when a gateway confirmed the current snapshot
    /// version (the one that includes the room); "Pending" when the storage committed and the
    /// version moved (the room's epoch is part of the content hash) but no gateway confirmed it
    /// yet; "Removed" when the room is no longer in the snapshot at all. A caller never sees
    /// "Synced" before the gateway confirms, so the storage commit and the route sync are
    /// distinct surfaces.</summary>
    public async Task<string> GetRoomRouteSyncStatusAsync(string tenantId, string gameId, string roomId,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await ReadRoutingSnapshotAsync(cancellationToken);
        var room = snapshot.Rooms.FirstOrDefault(route =>
            route.TenantId == tenantId && route.GameId == gameId && route.RoomId == roomId);
        if (room is null) return "Removed";
        return await IsRoutingVersionConfirmedAsync(snapshot.Version, cancellationToken)
            ? "Synced" : "Pending";
    }

    public async Task<OwnerStatus?> GetOwnershipAsync(string tenantId, string gameId, string kind,
        string entityId, CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT owner_process_instance_id,epoch,lease_until,status
            FROM control.ownership WHERE tenant_id=@tenant AND game_id=@game AND kind=@kind AND entity_id=@entity
            """, connection);
        BindOwner(command, tenantId, gameId, kind, entityId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new OwnerStatus(reader.GetString(0), reader.GetInt64(1), reader.GetDateTime(2), reader.GetString(3)) : null;
    }

    /// <summary>
    /// The open (still Transferred) room-recovery operation, or null (FIX-TAKE-04). A
    /// restarted control plane continues from here — a timed-out recovery RPC left the
    /// operation open, and continuing it never needs a second transfer or epoch bump.
    /// </summary>
    public async Task<RoomRecoveryOperation?> GetOpenRoomRecoveryAsync(string tenantId, string gameId,
        string roomId, CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT operation_id,from_instance,to_instance,target_epoch,phase,deadline_at,last_tick,last_error
            FROM control.recovery_operation
            WHERE tenant_id=@tenant AND game_id=@game AND room_id=@room AND phase='Transferred'
            ORDER BY created_at DESC LIMIT 1
            """, connection);
        command.Parameters.AddWithValue("tenant", tenantId);
        command.Parameters.AddWithValue("game", gameId);
        command.Parameters.AddWithValue("room", roomId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new RoomRecoveryOperation(tenantId, gameId, reader.GetString(0), roomId, reader.GetString(1),
                reader.GetString(2), reader.GetInt64(3), reader.GetString(4), reader.GetDateTime(5),
                reader.GetInt64(6), reader.IsDBNull(7) ? null : reader.GetString(7))
            : null;
    }

    /// <summary>
    /// Records a recovery attempt's outcome on the room's open operation row: the phase
    /// (Activated on success, Failed with the reason otherwise) and the committed progress.
    /// The row is progress, never authority — the ownership row stays the fence. A no-op
    /// when no open row exists, so rooms transferred before the record landed still recover.
    /// </summary>
    public async Task CompleteRoomRecoveryAsync(string tenantId, string gameId, string roomId,
        bool recovered, long lastTick, string? lastError, CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            UPDATE control.recovery_operation
            SET phase=@phase, last_tick=GREATEST(last_tick,@tick),
                last_error=@error, updated_at=clock_timestamp()
            WHERE tenant_id=@tenant AND game_id=@game AND room_id=@room AND phase='Transferred'
            """, connection);
        command.Parameters.AddWithValue("tenant", tenantId);
        command.Parameters.AddWithValue("game", gameId);
        command.Parameters.AddWithValue("room", roomId);
        command.Parameters.AddWithValue("phase", recovered ? "Activated" : "Failed");
        command.Parameters.AddWithValue("tick", lastTick);
        command.Parameters.AddWithValue("error", lastError is null ? DBNull.Value : lastError);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Open recovery operations past their deadline, oldest first (FIX-TAKE-05): the sweep's
    /// work list. Deadline comparison is database time — a target that keeps reporting live
    /// but never finishes its recovery appears here exactly like a silent one.
    /// </summary>
    public async Task<List<RoomRecoveryOperation>> ListExpiredOpenRoomRecoveriesAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT tenant_id,game_id,operation_id,room_id,from_instance,to_instance,
                target_epoch,phase,deadline_at,last_tick,last_error
            FROM control.recovery_operation
            WHERE phase='Transferred' AND deadline_at<clock_timestamp()
            ORDER BY deadline_at LIMIT 50
            """, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var expired = new List<RoomRecoveryOperation>();
        while (await reader.ReadAsync(cancellationToken))
            expired.Add(new RoomRecoveryOperation(reader.GetString(0), reader.GetString(1),
                reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5),
                reader.GetInt64(6), reader.GetString(7), reader.GetDateTime(8), reader.GetInt64(9),
                reader.IsDBNull(10) ? null : reader.GetString(10)));
        return expired;
    }

    /// <summary>FIX-SYNC-08: a paused room whose ready count reached the declared threshold —
    /// the resume leg's work list (authoritative-sync.md:53). The count and the threshold are
    /// both derived in SQL so the driver never parses a canonical; a room with no declared
    /// policy (an empty field) yields a NULL comparison and never qualifies.</summary>
    public sealed record ResumeReadyRoom(string RoomId, string TenantId, string GameId, long Epoch);

    /// <summary>FIX-SYNC-08: a paused room whose pause exceeded its declared maximum duration
    /// — the timeout leg's work list (authoritative-sync.md:54). Any paused room with a
    /// positive declared cap qualifies; a room with no declared cap (an empty field) never
    /// times out.</summary>
    public sealed record OverduePausedRoom(string RoomId, string TenantId, string GameId, long Epoch);

    public async Task<List<ResumeReadyRoom>> ListResumeReadyPausedRoomsAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT g.room_id,g.tenant_id,g.game_id,g.epoch
            FROM game.room g
            WHERE g.status='Paused'
              AND (SELECT count(*) FROM game.resume_ready r
                   WHERE r.room_id=g.room_id AND r.epoch=g.epoch)
                  >= NULLIF(split_part(g.offline_policy,'|',4),'')::int
            ORDER BY g.paused_at LIMIT 50
            """, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rooms = new List<ResumeReadyRoom>();
        while (await reader.ReadAsync(cancellationToken))
            rooms.Add(new ResumeReadyRoom(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.GetInt64(3)));
        return rooms;
    }

    public async Task<List<OverduePausedRoom>> ListOverduePausedRoomsAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT g.room_id,g.tenant_id,g.game_id,g.epoch
            FROM game.room g
            WHERE g.status='Paused' AND g.paused_at IS NOT NULL
              AND g.paused_at < clock_timestamp()
                  - NULLIF(split_part(g.offline_policy,'|',3),'')::int * interval '1 second'
            ORDER BY g.paused_at LIMIT 50
            """, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rooms = new List<OverduePausedRoom>();
        while (await reader.ReadAsync(cancellationToken))
            rooms.Add(new OverduePausedRoom(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.GetInt64(3)));
        return rooms;
    }

    /// <summary>
    /// How many recovery operations a room has ever recorded — the sweep's reassignment
    /// budget. A room that keeps timing out stops being moved automatically and waits for
    /// an operator instead of churning epochs forever.
    /// </summary>
    public async Task<int> CountRoomRecoveryOperationsAsync(string tenantId, string gameId,
        string roomId, CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT count(*) FROM control.recovery_operation
            WHERE tenant_id=@tenant AND game_id=@game AND room_id=@room
            """, connection);
        command.Parameters.AddWithValue("tenant", tenantId);
        command.Parameters.AddWithValue("game", gameId);
        command.Parameters.AddWithValue("room", roomId);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    }

    /// <summary>One room's reported progress (FIX-TAKE-06).</summary>
    public sealed record RoomProgressEntry(string RoomId, long Epoch, string State, long LastTick,
        long? LastCommittedUnixMs, string? StopReason);

    /// <summary>One stalled room as the stall query names it (FIX-TAKE-06). The tenant, game
    /// and prepared tick cadence ride along so governance can act without a second read
    /// (FIX-TAKE-07); they are null only when the room's own row is already gone.</summary>
    public sealed record StalledRoom(string ProcessInstanceId, string? TenantId, string? GameId,
        string RoomId, long Epoch, string State, long LastTick, int TickMs, string? StopReason);

    /// <summary>
    /// Records one report's per-room progress (FIX-TAKE-06), in one transaction with the
    /// pruning of rooms the process stopped naming. progress_at is database time and only
    /// moves when the reported head (state or tick) moves — a frozen tick across reports
    /// is the stall signal, never fresh evidence. An out-of-order report (a stale sequence)
    /// never regresses a row.
    /// </summary>
    public async Task RecordRoomProgressAsync(string processInstanceId, long reportSequence,
        IReadOnlyList<RoomProgressEntry> progress, CancellationToken cancellationToken = default)
    {
        if (reportSequence < 0) throw new ArgumentOutOfRangeException(nameof(reportSequence));
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        foreach (var entry in progress)
        {
            await using var upsert = new NpgsqlCommand("""
                INSERT INTO control.room_progress(process_instance_id,room_id,epoch,state,
                    last_tick,last_committed_at,stop_reason,report_sequence)
                VALUES (@process,@room,@epoch,@state,@tick,
                    CASE WHEN @ms>0 THEN to_timestamp(@ms::double precision/1000.0) END,
                    @reason,@seq)
                ON CONFLICT (process_instance_id, room_id) DO UPDATE SET
                    epoch=excluded.epoch, state=excluded.state, last_tick=excluded.last_tick,
                    last_committed_at=excluded.last_committed_at, stop_reason=excluded.stop_reason,
                    report_sequence=excluded.report_sequence, reported_at=clock_timestamp(),
                    progress_at=CASE WHEN excluded.state=control.room_progress.state
                            AND excluded.last_tick=control.room_progress.last_tick
                        THEN control.room_progress.progress_at ELSE clock_timestamp() END
                WHERE excluded.report_sequence >= control.room_progress.report_sequence
                """, connection, transaction);
            upsert.Parameters.AddWithValue("process", processInstanceId);
            upsert.Parameters.AddWithValue("room", entry.RoomId);
            upsert.Parameters.AddWithValue("epoch", entry.Epoch);
            upsert.Parameters.AddWithValue("state", entry.State);
            upsert.Parameters.AddWithValue("tick", entry.LastTick);
            upsert.Parameters.AddWithValue("ms", entry.LastCommittedUnixMs ?? 0L);
            upsert.Parameters.AddWithValue("reason",
                (object?)entry.StopReason ?? DBNull.Value);
            upsert.Parameters.AddWithValue("seq", reportSequence);
            await upsert.ExecuteNonQueryAsync(cancellationToken);
        }
        // A room the process no longer names has no reporter; keeping its row would let a
        // dead report masquerade as live evidence.
        await using var prune = new NpgsqlCommand(
            "DELETE FROM control.room_progress WHERE process_instance_id=@process AND NOT (room_id = ANY(@rooms))",
            connection, transaction);
        prune.Parameters.AddWithValue("process", processInstanceId);
        prune.Parameters.AddWithValue("rooms",
            progress.Select(entry => entry.RoomId).ToArray());
        await prune.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>
    /// The rooms whose gameplay stopped while their host's heartbeat stayed fresh (FIX-TAKE-06,
    /// the identification half; the governance policy is FIX-TAKE-07). A silently frozen
    /// Running actor (database-time progress older than the threshold) and an explicitly
    /// Stopped actor both appear; a business-paused room — its actor parked by the store's
    /// suspension rejection — is not faulty merely because its tick froze, so that exact
    /// park reason is excluded. A completed round is normal, never stalled.
    /// </summary>
    public async Task<List<StalledRoom>> ListStalledRoomsAsync(int stallSeconds, int freshSeconds,
        CancellationToken cancellationToken = default)
    {
        if (stallSeconds <= 0) throw new ArgumentOutOfRangeException(nameof(stallSeconds));
        if (freshSeconds <= 0) throw new ArgumentOutOfRangeException(nameof(freshSeconds));
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT r.process_instance_id, g.tenant_id, g.game_id, r.room_id, r.epoch, r.state,
                r.last_tick, g.tick_ms, r.stop_reason
            FROM control.room_progress r
            JOIN control.process p ON p.process_instance_id = r.process_instance_id
            LEFT JOIN game.room g ON g.room_id = r.room_id
            WHERE p.observed_at > clock_timestamp() - @fresh * interval '1 second'
              AND ( (r.state = 'Running'
                        AND r.progress_at < clock_timestamp() - @stall * interval '1 second')
                 OR r.state = 'Stopped' )
              AND NOT ( r.state = 'Stopped'
                    AND r.stop_reason LIKE '%A room in status Suspended does not accept frame commits%'
                    AND coalesce(g.status, '') = 'Suspended' )
            ORDER BY r.progress_at
            """, connection);
        command.Parameters.AddWithValue("stall", stallSeconds);
        command.Parameters.AddWithValue("fresh", freshSeconds);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var stalled = new List<StalledRoom>();
        while (await reader.ReadAsync(cancellationToken))
            stalled.Add(new StalledRoom(reader.GetString(0),
                await reader.IsDBNullAsync(1, cancellationToken) ? null : reader.GetString(1),
                await reader.IsDBNullAsync(2, cancellationToken) ? null : reader.GetString(2),
                reader.GetString(3), reader.GetInt64(4), reader.GetString(5), reader.GetInt64(6),
                await reader.IsDBNullAsync(7, cancellationToken) ? 0 : reader.GetInt32(7),
                await reader.IsDBNullAsync(8, cancellationToken) ? null : reader.GetString(8)));
        return stalled;
    }

    /// <summary>
    /// Extends the lease of every Active ownership whose owner still reports live (architecture
    /// 4.2: the registrar renews on the owner's behalf while the owner is observable). Live
    /// means Running or Draining (FIX-SCHED-02): a drained host keeps serving the rooms it
    /// holds, so their leases keep renewing — drain stops NEW assignments, never the rooms
    /// already running. An owner whose process row went stale is not renewed — its lease runs
    /// out and the entity becomes takeable. A legacy owner without any process row is never
    /// renewed: it keeps its own renewal discipline.
    /// </summary>
    public async Task<int> RenewLiveOwnershipsAsync(int leaseSeconds, int gateSeconds,
        CancellationToken cancellationToken = default)
    {
        if (leaseSeconds is <= 0 or > 60) throw new ArgumentOutOfRangeException(nameof(leaseSeconds));
        if (gateSeconds <= 0) throw new ArgumentOutOfRangeException(nameof(gateSeconds));
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            UPDATE control.ownership AS ownership
            SET lease_until=clock_timestamp()+@lease*interval '1 second', version=ownership.version+1
            FROM control.process AS process
            WHERE ownership.status='Active'
              AND process.process_instance_id=ownership.owner_process_instance_id
              AND process.lifecycle IN ('Running','Draining')
              AND process.observed_at>=clock_timestamp()-@gate*interval '1 second'
            """, connection);
        command.Parameters.AddWithValue("lease", leaseSeconds);
        command.Parameters.AddWithValue("gate", gateSeconds);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Control-plane handover of a single-domain ownership (architecture 10.3, the streamer
    /// form): bumps the epoch, moves the holder and parks the row Recovering in one transaction.
    /// Until the new holder's own claim confirms the row (same holder with a live lease keeps
    /// the epoch and returns it Active), the renewal registrar leaves it alone — a handover
    /// nobody completes expires and becomes takeable again. The old holder's data-plane writes
    /// for a streamer entity are fenced by the session domain, not by this row: rotation already
    /// replaced the session it might speak for. The <paramref name="fromInstance"/> guard makes
    /// a stale command visible instead of moving an entity that changed hands meanwhile.
    /// </summary>
    public async Task<OwnerStatus> TransferOwnershipAsync(string tenantId, string gameId, string kind,
        string entityId, string fromInstance, string toInstance, int leaseSeconds,
        CancellationToken cancellationToken = default)
    {
        if (leaseSeconds is <= 0 or > 60) throw new ArgumentOutOfRangeException(nameof(leaseSeconds));
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var lockCommand = new NpgsqlCommand("""
            SELECT owner_process_instance_id,status FROM control.ownership
            WHERE tenant_id=@tenant AND game_id=@game AND kind=@kind AND entity_id=@entity
            FOR UPDATE
            """, connection, transaction))
        {
            BindOwner(lockCommand, tenantId, gameId, kind, entityId);
            await using var reader = await lockCommand.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                throw new InvalidOperationException("The ownership vanished mid-transfer.");
            var holder = reader.GetString(0);
            var status = reader.GetString(1);
            if (!StringComparer.Ordinal.Equals(holder, fromInstance))
                throw new InvalidOperationException($"The entity is held by {holder}, not {fromInstance}.");
            if (!StringComparer.Ordinal.Equals(status, "Active"))
                throw new InvalidOperationException("The ownership is not Active; it cannot transfer.");
            await reader.DisposeAsync();
        }
        await using (var command = new NpgsqlCommand("""
            UPDATE control.ownership
            SET owner_process_instance_id=@to,epoch=epoch+1,
                lease_until=clock_timestamp()+@lease*interval '1 second',
                status='Recovering',version=version+1
            WHERE tenant_id=@tenant AND game_id=@game AND kind=@kind AND entity_id=@entity
            RETURNING owner_process_instance_id,epoch,lease_until,status
            """, connection, transaction))
        {
            BindOwner(command, tenantId, gameId, kind, entityId);
            command.Parameters.AddWithValue("to", toInstance);
            command.Parameters.AddWithValue("lease", leaseSeconds);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                throw new InvalidOperationException("The ownership vanished mid-transfer.");
            var takeover = new OwnerStatus(reader.GetString(0), reader.GetInt64(1),
                reader.GetDateTime(2), reader.GetString(3));
            await reader.DisposeAsync();
            await transaction.CommitAsync(cancellationToken);
            return takeover;
        }
    }

    private static void BindOwner(NpgsqlCommand command, string tenantId, string gameId, string kind, string entityId)
    {
        command.Parameters.AddWithValue("tenant", tenantId);
        command.Parameters.AddWithValue("game", gameId);
        command.Parameters.AddWithValue("kind", kind);
        command.Parameters.AddWithValue("entity", entityId);
    }

    /// <summary>
    /// GM room-epoch bump (FIX-GM-02, architecture 4.5): one transaction validates that the
    /// ownership is Active and that BOTH epoch columns agree with the caller's expected value,
    /// then moves both to the resolved target (unstated target = expected + 1, the FIX-GM-01
    /// rule), keeping the owner and parking the ownership Recovering, and records the idempotent
    /// receipt and the audit row in the same transaction. A retry by operation id returns the
    /// recorded outcome without a second bump; a different request under the same id is an
    /// <see cref="Chorus.Data.IdempotencyConflictException"/>. Two concurrent bumps of the same
    /// expected epoch serialize on the ownership row, so exactly one logical operation succeeds;
    /// a rollback leaves both columns at their pre-bump value. The room is recovered explicitly
    /// afterwards (FIX-GM-05), never by the takeover sweep — the receipt lives apart from
    /// <c>control.recovery_operation</c> for exactly that reason.
    /// </summary>
    public async Task<EpochBumpOutcome> BumpRoomEpochAsync(string tenantId, string gameId, string roomId,
        long expectedEpoch, long targetEpoch, string operationId, string operatorId, string reason,
        int leaseSeconds, CancellationToken cancellationToken = default)
    {
        RoomEpochRule.ValidateIdentifiers(tenantId, gameId, roomId);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId, nameof(operationId));
        ArgumentException.ThrowIfNullOrWhiteSpace(operatorId, nameof(operatorId));
        if (leaseSeconds is <= 0 or > 60) throw new ArgumentOutOfRangeException(nameof(leaseSeconds));
        var resolvedTarget = RoomEpochRule.ResolveTarget(expectedEpoch, targetEpoch);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        // Idempotency first: a replay returns the recorded outcome, never a second bump. The
        // request shape (expected, resolved target) is the identity — the same id under a
        // different shape is a conflict.
        await using (var replay = new NpgsqlCommand("""
            SELECT owner_instance_id,old_epoch,new_epoch,expected_epoch,target_epoch
            FROM control.epoch_operation
            WHERE tenant_id=@tenant AND game_id=@game AND operation_id=@operation
            """, connection))
        {
            replay.Parameters.AddWithValue("tenant", tenantId);
            replay.Parameters.AddWithValue("game", gameId);
            replay.Parameters.AddWithValue("operation", operationId);
            await using var reader = await replay.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                var owner = reader.GetString(0);
                var old = reader.GetInt64(1);
                var @new = reader.GetInt64(2);
                var recordedExpected = reader.GetInt64(3);
                var recordedTarget = reader.GetInt64(4);
                await reader.DisposeAsync();
                if (recordedExpected != expectedEpoch || recordedTarget != resolvedTarget)
                    throw new Chorus.Data.IdempotencyConflictException();
                return new EpochBumpOutcome(old, @new, owner, "Recovering", true);
            }
        }

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        string holder;
        await using (var lockCommand = new NpgsqlCommand("""
            SELECT owner_process_instance_id,status,epoch FROM control.ownership
            WHERE tenant_id=@tenant AND game_id=@game AND kind='room' AND entity_id=@room
            FOR UPDATE
            """, connection, transaction))
        {
            lockCommand.Parameters.AddWithValue("tenant", tenantId);
            lockCommand.Parameters.AddWithValue("game", gameId);
            lockCommand.Parameters.AddWithValue("room", roomId);
            await using var reader = await lockCommand.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                throw new InvalidOperationException("The ownership row is missing for this room.");
            holder = reader.GetString(0);
            var status = reader.GetString(1);
            var current = reader.GetInt64(2);
            await reader.DisposeAsync();
            if (!StringComparer.Ordinal.Equals(status, "Active"))
                throw new InvalidOperationException($"The ownership is {status}; only an Active room bumps.");
            if (current != expectedEpoch)
                throw new InvalidOperationException($"The ownership epoch is {current}, not {expectedEpoch}.");
        }
        // Architecture 4.5: both columns must agree before the bump — an inconsistent pair is an
        // invariant break the operator must see, never something the bump papers over.
        await using (var roomCheck = new NpgsqlCommand("""
            SELECT epoch FROM game.room
            WHERE room_id=@room AND tenant_id=@tenant AND game_id=@game
            """, connection, transaction))
        {
            roomCheck.Parameters.AddWithValue("room", roomId);
            roomCheck.Parameters.AddWithValue("tenant", tenantId);
            roomCheck.Parameters.AddWithValue("game", gameId);
            var roomEpoch = await roomCheck.ExecuteScalarAsync(cancellationToken);
            if (roomEpoch is null)
                throw new InvalidOperationException("The game.room row is missing for this room.");
            if (Convert.ToInt64(roomEpoch) != expectedEpoch)
                throw new InvalidOperationException(
                    $"The room epoch is {roomEpoch}, not {expectedEpoch}; ownership and room disagree.");
        }
        await using (var bump = new NpgsqlCommand("""
            UPDATE control.ownership
            SET epoch=@target, status='Recovering',
                lease_until=clock_timestamp()+@lease*interval '1 second', version=version+1
            WHERE tenant_id=@tenant AND game_id=@game AND kind='room' AND entity_id=@room
              AND epoch=@expected AND status='Active'
            """, connection, transaction))
        {
            bump.Parameters.AddWithValue("target", resolvedTarget);
            bump.Parameters.AddWithValue("expected", expectedEpoch);
            bump.Parameters.AddWithValue("lease", leaseSeconds);
            bump.Parameters.AddWithValue("tenant", tenantId);
            bump.Parameters.AddWithValue("game", gameId);
            bump.Parameters.AddWithValue("room", roomId);
            if (await bump.ExecuteNonQueryAsync(cancellationToken) != 1)
                throw new InvalidOperationException("The ownership epoch moved concurrently; no bump applied.");
        }
        await using (var room = new NpgsqlCommand("""
            UPDATE game.room SET epoch=@target, updated_at=clock_timestamp()
            WHERE room_id=@room AND tenant_id=@tenant AND game_id=@game
              AND epoch=@expected AND status IN ('Running','Finishing')
            """, connection, transaction))
        {
            room.Parameters.AddWithValue("target", resolvedTarget);
            room.Parameters.AddWithValue("expected", expectedEpoch);
            room.Parameters.AddWithValue("room", roomId);
            room.Parameters.AddWithValue("tenant", tenantId);
            room.Parameters.AddWithValue("game", gameId);
            if (await room.ExecuteNonQueryAsync(cancellationToken) != 1)
                throw new InvalidOperationException("The room is not in a bumpable state; no bump applied.");
        }
        await using (var receipt = new NpgsqlCommand("""
            INSERT INTO control.epoch_operation(tenant_id,game_id,operation_id,room_id,owner_instance_id,
                expected_epoch,target_epoch,old_epoch,new_epoch,reason)
            VALUES (@tenant,@game,@operation,@room,@owner,@expected,@target,@old,@new,@reason)
            """, connection, transaction))
        {
            receipt.Parameters.AddWithValue("tenant", tenantId);
            receipt.Parameters.AddWithValue("game", gameId);
            receipt.Parameters.AddWithValue("operation", operationId);
            receipt.Parameters.AddWithValue("room", roomId);
            receipt.Parameters.AddWithValue("owner", holder);
            receipt.Parameters.AddWithValue("expected", expectedEpoch);
            receipt.Parameters.AddWithValue("target", resolvedTarget);
            receipt.Parameters.AddWithValue("old", expectedEpoch);
            receipt.Parameters.AddWithValue("new", resolvedTarget);
            receipt.Parameters.AddWithValue("reason", reason);
            await receipt.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var audit = new NpgsqlCommand("""
            INSERT INTO infra.audit(actor_instance_id,operation_id,action,target,reason)
            VALUES (@actor,@operation,'RoomEpochBump',@room,@reason)
            """, connection, transaction))
        {
            audit.Parameters.AddWithValue("actor", operatorId);
            audit.Parameters.AddWithValue("operation", operationId);
            audit.Parameters.AddWithValue("room", roomId);
            audit.Parameters.AddWithValue("reason", reason);
            await audit.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return new EpochBumpOutcome(expectedEpoch, resolvedTarget, holder, "Recovering", false);
    }

    /// <summary>
    /// Reads the versioned full routing snapshot (architecture 4.4): rooms with owner epochs and
    /// endpoints, live streamer bindings with their rooms, and every registered instance. The
    /// version is a 64-bit content hash — a deliberate deviation from a monotonic sequence: any
    /// mutation of the routed content produces a different version, so a consumer that missed
    /// Watch notices detects any gap by comparing versions and re-pulls instead of trusting a
    /// stale snapshot. Routing carries endpoints, instances and epochs only — never secrets.
    /// </summary>
    public async Task<RoutingSnapshot> ReadRoutingSnapshotAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        var rooms = new List<RoomRoute>();
        // FIX-END-03: a completed round stays published (Finishing): late readers resolve the
        // room until the end sequence retires its route — completion itself is not destruction.
        // FIX-END-16: a prepared-but-not-yet-activated room publishes its route too (architecture
        // 4.4 battle-start barrier) — the control plane must confirm the platform gateway
        // installed the route BEFORE it activates the battle, so the route has to be visible and
        // confirmable while the room still stands at Tick 0. The gateway may resolve a preparing
        // room and push input to it; the data plane accepts Preparing (FIX-END-06) and the held
        // actor's inbox waits for the activation start line, so input is held, not lost.
        await using (var command = new NpgsqlCommand("""
            SELECT o.entity_id,o.tenant_id,o.game_id,o.owner_process_instance_id,o.epoch,o.status,p.endpoint
            FROM control.ownership o
            JOIN game.room r ON r.room_id=o.entity_id AND r.tenant_id=o.tenant_id AND r.game_id=o.game_id
            LEFT JOIN control.process p ON p.process_instance_id=o.owner_process_instance_id
            WHERE o.kind='room' AND r.status IN ('Preparing','Running','Finishing')
            ORDER BY o.tenant_id,o.game_id,o.entity_id
            """, connection))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
                rooms.Add(new RoomRoute(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                    reader.GetString(3), reader.GetInt64(4),
                    reader.IsDBNull(6) ? null : reader.GetString(6), reader.GetString(5)));
        }

        var streamers = new List<StreamerRoute>();
        await using (var command = new NpgsqlCommand("""
            SELECT s.streamer_id,b.binding_id,b.current_room_id,b.state,
                o.owner_process_instance_id,COALESCE(o.epoch,0),
                s.platform,s.canonical_live_session_key,b.generation,b.round_generation,
                b.tenant_id
            FROM live.binding b
            JOIN live.session s ON s.live_session_id=b.live_session_id
            LEFT JOIN control.ownership o ON o.kind='streamer' AND o.tenant_id=b.tenant_id
                AND o.game_id=b.game_id AND o.entity_id=s.streamer_id
            WHERE b.state IN ('Starting','Ready','Running','AwaitingPlatformClose')
            ORDER BY s.streamer_id,b.binding_id
            """, connection))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
                streamers.Add(new StreamerRoute(reader.GetString(0), reader.GetString(1),
                    reader.IsDBNull(2) ? "" : reader.GetString(2), reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetInt64(5),
                    reader.GetString(6), reader.GetString(7), reader.GetInt64(8),
                    reader.GetInt64(9), reader.GetString(10)));
        }

        var instances = new List<InstanceRoute>();
        await using (var command = new NpgsqlCommand("""
            SELECT p.process_instance_id,d.role,p.node_id,p.slot_id,p.endpoint,p.readiness,p.admission
            FROM control.process p
            JOIN control.deployment d ON d.slot_id=p.slot_id
            ORDER BY p.process_instance_id
            """, connection))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
                instances.Add(new InstanceRoute(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                    reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.GetString(5), reader.GetString(6)));
        }

        var version = ContentHash(rooms.Select(r =>
            $"room|{r.RoomId}|{r.TenantId}|{r.GameId}|{r.OwnerInstanceId}|{r.Epoch}|{r.Endpoint ?? ""}|{r.Status}")
            .Concat(streamers.Select(s =>
                $"streamer|{s.StreamerId}|{s.BindingId}|{s.RoomId}|{s.State}|{s.OwnerInstanceId ?? ""}|{s.Epoch}|{s.Platform}|{s.CanonicalLiveSessionKey}|{s.BindingGeneration}|{s.RoundGeneration}|{s.TenantId}"))
            .Concat(instances.Select(i =>
                $"instance|{i.ProcessInstanceId}|{i.Role}|{i.NodeId}|{i.SlotId}|{i.Endpoint ?? ""}|{i.Readiness}|{i.Admission}")));
        return new RoutingSnapshot(version, rooms, streamers, instances);
    }

    /// <summary>
    /// FIX-END-16: waits until a platform gateway has installed and confirmed the routing
    /// version that carries the given room (architecture 4.4 battle-start barrier). The room
    /// publishes its route while Preparing, so once it stands in the current snapshot and a
    /// gateway has confirmed exactly that version, the route is installed — the control plane
    /// may then activate the battle. The confirm is a per-gateway global version (schema 0024),
    /// so the check is "some gateway confirmed the snapshot's current version"; the room's
    /// presence in that snapshot is what ties the version to this room. Times out false: the
    /// route never confirmed, and the caller must not start the battle.
    /// </summary>
    public async Task<bool> WaitForRoomRouteConfirmedAsync(string roomId, TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            var snapshot = await ReadRoutingSnapshotAsync(cancellationToken);
            if (snapshot.Rooms.Any(room => room.RoomId == roomId)
                && await IsRoutingVersionConfirmedAsync(snapshot.Version, cancellationToken))
                return true;
            if (DateTime.UtcNow >= deadline) return false;
            await Task.Delay(RouteConfirmPollMs, cancellationToken);
        }
    }

    private const int RouteConfirmPollMs = 150;

    private async Task<bool> IsRoutingVersionConfirmedAsync(long version,
        CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            "SELECT 1 FROM control.routing_confirm WHERE version=@version LIMIT 1", connection);
        command.Parameters.AddWithValue("version", version);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    /// <summary>
    /// FIX-END-16 boundary: whether the cluster has deployed a platform gateway at all. The
    /// battle-start barrier (architecture 4.4) waits for a gateway to confirm the room's route
    /// before it activates the battle, because the gateway is the delivery path the battle's
    /// input flows through — no confirmed route means a gateway might deliver input into a
    /// battle that already ticked past it. A cluster with no gateway registered has no delivery
    /// path to wait for (there is no input to lose, and a gateway that joins later installs the
    /// published route and delivers from then on), so the coordinator applies the gate only
    /// once a gateway is actually registered. A gateway registers by reporting a process whose
    /// deployment role is the platform gateway; the report lands only under a pre-registered
    /// node (ReportProcessAsync), so a bare cluster reports none.
    /// </summary>
    public async Task<bool> HasRegisteredGatewayAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT 1 FROM control.process p
            JOIN control.deployment d ON d.slot_id=p.slot_id
            WHERE d.role='platform-gateway' LIMIT 1
            """, connection);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    /// <summary>FNV-1a over the canonical snapshot lines; collisions are acceptable — the version
    /// only signals "content changed", it is not an identity.</summary>
    private static long ContentHash(IEnumerable<string> lines)
    {
        unchecked
        {
            const long prime = 1099511628211;
            var hash = -3750763034362895579L; // 14695981039346656037 as int64
            foreach (var line in lines)
            {
                foreach (var c in line)
                {
                    hash ^= c;
                    hash *= prime;
                }
                hash ^= '\n';
                hash *= prime;
            }
            return hash;
        }
    }

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

    /// <summary>
    /// Begins the room's end operation (FIX-END-01). Creation is idempotent by request
    /// identity: the same id with the same room and generations replays the recorded
    /// operation, a second id for the same room refuses, and the same id naming another
    /// room refuses. The generation fence runs before any write — a begin naming a round
    /// generation that is not exactly what the room's participant bindings currently hold
    /// is late evidence from a round the mounts have moved past, and it writes nothing.
    /// </summary>
    public async Task<(EndOperation Operation, bool Duplicate)> BeginEndOperationAsync(
        string tenantId, string gameId, string roomId, string endOperationId,
        long finalTick, long resultVersion, CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        // The replay path first: an existing row answers by identity without touching the
        // fences, so a retry after a lost reply — or after the mounts released the round —
        // still replays the one operation.
        await using (var existing = new NpgsqlCommand("""
            SELECT room_id,round_generation,final_tick,result_version
            FROM control.end_operation
            WHERE tenant_id=@tenant AND game_id=@game AND end_operation_id=@operation
            """, connection, transaction))
        {
            existing.Parameters.AddWithValue("tenant", tenantId);
            existing.Parameters.AddWithValue("game", gameId);
            existing.Parameters.AddWithValue("operation", endOperationId);
            await using var reader = await existing.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                var recordedRoom = reader.GetString(0);
                if (recordedRoom != roomId)
                    throw new EndOperationConflictException(
                        $"The end operation {endOperationId} belongs to the room {recordedRoom}.");
                await reader.DisposeAsync();
                var replayed = await ReadEndOperationAsync(
                    connection, transaction, tenantId, gameId, endOperationId, cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return (replayed!, true);
            }
        }

        // One operation per round: a second business id for the same room refuses visibly
        // instead of racing two coordinators through the same steps.
        await using (var room = new NpgsqlCommand(
            "SELECT 1 FROM control.end_operation WHERE tenant_id=@tenant AND game_id=@game AND room_id=@room",
            connection, transaction))
        {
            room.Parameters.AddWithValue("tenant", tenantId);
            room.Parameters.AddWithValue("game", gameId);
            room.Parameters.AddWithValue("room", roomId);
            if (await room.ExecuteScalarAsync(cancellationToken) is not null)
                throw new EndOperationConflictException(
                    $"The room {roomId} already has its end operation.");
        }

        // The lateness fence (FIX-END-04): the room's participant bindings must still point at
        // this room. A binding that moved on — its pointer moved to another room or was
        // released — is evidence the mounts entered a newer round, and the late begin writes
        // nothing. The generations are derived in this transaction from the mounts that still
        // sit here: per-binding counters diverge as mounts carry round history into a room, so
        // no caller can name one generation that satisfies every mount — the record takes the
        // evidence the begin actually found, not what the caller claims.
        var generations = new List<EndMountGeneration>();
        await using (var participants = new NpgsqlCommand("""
            SELECT b.binding_id,b.generation,b.round_generation,b.current_room_id
            FROM game.participation p JOIN live.binding b ON b.binding_id=p.binding_id
            WHERE p.room_id=@room
            ORDER BY b.binding_id
            """, connection, transaction))
        {
            participants.Parameters.AddWithValue("room", roomId);
            await using var reader = await participants.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                if (reader.IsDBNull(3) || reader.GetString(3) != roomId)
                    throw new StaleRoundGenerationException(
                        $"The mount {reader.GetString(0)} no longer points at the room {roomId}; " +
                        "the begin is late.");
                generations.Add(new EndMountGeneration(
                    reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2)));
            }
        }
        if (generations.Count == 0)
            throw new StaleRoundGenerationException(
                $"The room {roomId} has no participant mounts; the begin is late.");
        var roundGeneration = generations.Max(mount => mount.RoundGeneration);

        await using (var insert = new NpgsqlCommand("""
            INSERT INTO control.end_operation(tenant_id,game_id,end_operation_id,room_id,
                round_generation,binding_generations,final_tick,result_version,phase)
            VALUES (@tenant,@game,@operation,@room,@generation,@bindings::jsonb,@tick,@version,@phase)
            """, connection, transaction))
        {
            insert.Parameters.AddWithValue("tenant", tenantId);
            insert.Parameters.AddWithValue("game", gameId);
            insert.Parameters.AddWithValue("operation", endOperationId);
            insert.Parameters.AddWithValue("room", roomId);
            insert.Parameters.AddWithValue("generation", roundGeneration);
            insert.Parameters.AddWithValue("bindings", JsonSerializer.Serialize(
                generations.Select(binding => new
                {
                    bindingId = binding.BindingId, generation = binding.Generation,
                    roundGeneration = binding.RoundGeneration
                })));
            insert.Parameters.AddWithValue("tick", finalTick);
            insert.Parameters.AddWithValue("version", resultVersion);
            insert.Parameters.AddWithValue("phase", EndOperationPhases.First);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
        var created = await ReadEndOperationAsync(
            connection, transaction, tenantId, gameId, endOperationId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return (created!, false);
    }

    /// <summary>
    /// Rooms whose result froze but whose end operation never landed (FIX-END-04): the game
    /// host's step-1 report is the fast path, this query is the net — a host killed after the
    /// final commit, or a report whose retries all failed, still yields the operation. The
    /// evidence comes from the room's own persisted receipt (last tick and state version),
    /// and the grace keeps the sweep behind the live host's own report: a racing begin on the
    /// same business id replays one row either way.
    /// </summary>
    public async Task<List<UnreportedFinishingRoom>> ListUnreportedResultRoomsAsync(
        int graceSeconds, CancellationToken cancellationToken = default)
    {
        if (graceSeconds < 0) throw new ArgumentOutOfRangeException(nameof(graceSeconds));
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT g.tenant_id,g.game_id,g.room_id,g.last_tick,g.state_version
            FROM game.room g
            WHERE g.status IN ('Finishing','Settled','Closed')
              AND g.updated_at < clock_timestamp() - @grace * interval '1 second'
              AND NOT EXISTS (
                  SELECT 1 FROM control.end_operation o
                  WHERE o.tenant_id=g.tenant_id AND o.game_id=g.game_id AND o.room_id=g.room_id)
            ORDER BY g.tenant_id,g.game_id,g.room_id
            """, connection);
        command.Parameters.AddWithValue("grace", graceSeconds);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rooms = new List<UnreportedFinishingRoom>();
        while (await reader.ReadAsync(cancellationToken))
            rooms.Add(new UnreportedFinishingRoom(reader.GetString(0), reader.GetString(1),
                reader.GetString(2), reader.GetInt64(3), reader.GetInt64(4)));
        return rooms;
    }

    /// <summary>The settle executor's domain walk (FIX-END-11): every tenant and game that
    /// still holds a Finishing room. This read only names where to ask — the queue's truth
    /// stays behind the data plane's ListPendingSettlements face.</summary>
    public async Task<List<SettleDomain>> ListSettleDomainsAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
"""
            SELECT DISTINCT tenant_id,game_id FROM game.room WHERE status='Finishing'
            ORDER BY tenant_id,game_id
""", connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var domains = new List<SettleDomain>();
        while (await reader.ReadAsync(cancellationToken))
            domains.Add(new SettleDomain(reader.GetString(0), reader.GetString(1)));
        return domains;
    }

    /// <summary>
    /// Atomically claims the room's settlement task for one executor (FIX-SETTLE-03). The
    /// insert creates the task on first discovery; the conflict path re-claims only a task
    /// that is Retryable and due, or Executing whose lease expired — a live lease, an
    /// unexpired backoff or a NeedsAttention task is left untouched and the claim returns
    /// null. The attempt counter bumps on every claim, so a reclaim after a killed holder
    /// is a new attempt with the same idempotent floor under it.
    /// </summary>
    public async Task<SettleTaskState?> ClaimSettleTaskAsync(string tenantId, string gameId,
        string roomId, string instanceId, TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentNullException.ThrowIfNullOrWhiteSpace(gameId);
        ArgumentNullException.ThrowIfNullOrWhiteSpace(roomId);
        ArgumentNullException.ThrowIfNullOrWhiteSpace(instanceId);
        if (leaseDuration <= TimeSpan.Zero)
            throw new ArgumentException("The lease duration must be positive.", nameof(leaseDuration));
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            INSERT INTO control.settle_task
                (room_id,tenant_id,game_id,status,owner_instance_id,lease_until,attempt_count,next_attempt,last_error)
            VALUES (@room,@tenant,@game,'Executing',@owner,@until,1,NULL,NULL)
            ON CONFLICT (room_id) DO UPDATE
            SET status='Executing', owner_instance_id=@owner, lease_until=@until,
                attempt_count=control.settle_task.attempt_count+1,
                next_attempt=NULL, last_error=NULL, updated_at=clock_timestamp()
            WHERE (control.settle_task.status='Retryable'
                    AND (control.settle_task.next_attempt IS NULL
                         OR control.settle_task.next_attempt<=clock_timestamp()))
               OR (control.settle_task.status='Executing'
                    AND control.settle_task.lease_until IS NOT NULL
                    AND control.settle_task.lease_until<clock_timestamp())
            RETURNING room_id,status,owner_instance_id,lease_until,attempt_count,last_error,next_attempt
            """, connection);
        command.Parameters.AddWithValue("room", roomId);
        command.Parameters.AddWithValue("tenant", tenantId);
        command.Parameters.AddWithValue("game", gameId);
        command.Parameters.AddWithValue("owner", instanceId);
        command.Parameters.AddWithValue("until", DateTime.UtcNow + leaseDuration);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadSettleTask(reader) : null;
    }

    /// <summary>
    /// Records a failed attempt (FIX-SETTLE-03): the row becomes Retryable with a bounded
    /// exponential next attempt, or NeedsAttention once the attempt budget is spent — the
    /// failure reason stays on the row either way. Only the current lease holder may record
    /// (a stale holder whose lease was reclaimed cannot overwrite the new holder's state).
    /// </summary>
    public async Task RecordSettleFailureAsync(string roomId, string instanceId, string error,
        int maxAttempts, TimeSpan backoff, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNullOrWhiteSpace(roomId);
        ArgumentNullException.ThrowIfNullOrWhiteSpace(instanceId);
        ArgumentNullException.ThrowIfNullOrWhiteSpace(error);
        if (maxAttempts <= 0) throw new ArgumentOutOfRangeException(nameof(maxAttempts));
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            UPDATE control.settle_task
            SET status=CASE WHEN attempt_count>=@max THEN 'NeedsAttention' ELSE 'Retryable' END,
                last_error=@error,
                next_attempt=CASE WHEN attempt_count>=@max THEN NULL
                                  ELSE clock_timestamp()+make_interval(secs=>@backoff) END,
                lease_until=NULL, owner_instance_id=NULL, updated_at=clock_timestamp()
            WHERE room_id=@room AND owner_instance_id=@owner
            """, connection);
        command.Parameters.AddWithValue("room", roomId);
        command.Parameters.AddWithValue("owner", instanceId);
        command.Parameters.AddWithValue("error", error);
        command.Parameters.AddWithValue("max", maxAttempts);
        command.Parameters.AddWithValue("backoff", backoff.TotalSeconds);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>The room's grants are fully applied — the task is obsolete and leaves the
    /// table, so the settle_task set stays proportional to in-flight and failed work.</summary>
    public async Task CompleteSettleTaskAsync(string roomId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNullOrWhiteSpace(roomId);
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            "DELETE FROM control.settle_task WHERE room_id=@room", connection);
        command.Parameters.AddWithValue("room", roomId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>The queryable settlement task state for a room (FIX-SETTLE-03): the status,
    /// the attempt count, the last failure reason and the next attempt. Null when the room
    /// has no task — either never discovered or already settled.</summary>
    public async Task<SettleTaskState?> GetSettleTaskAsync(string roomId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNullOrWhiteSpace(roomId);
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT room_id,status,owner_instance_id,lease_until,attempt_count,last_error,next_attempt
            FROM control.settle_task WHERE room_id=@room
            """, connection);
        command.Parameters.AddWithValue("room", roomId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadSettleTask(reader) : null;
    }

    private static SettleTaskState ReadSettleTask(NpgsqlDataReader reader) => new(
        reader.GetString(0), reader.GetString(1),
        reader.IsDBNull(2) ? null : reader.GetString(2),
        reader.IsDBNull(3) ? null : reader.GetDateTime(3),
        reader.GetInt32(4),
        reader.IsDBNull(5) ? null : reader.GetString(5),
        reader.IsDBNull(6) ? null : reader.GetDateTime(6));

    /// <summary>The room's end operation, whatever phase it stands at.</summary>
    public async Task<EndOperation?> GetEndOperationAsync(string tenantId, string gameId,
        string roomId, CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT end_operation_id FROM control.end_operation
            WHERE tenant_id=@tenant AND game_id=@game AND room_id=@room
            """, connection);
        command.Parameters.AddWithValue("tenant", tenantId);
        command.Parameters.AddWithValue("game", gameId);
        command.Parameters.AddWithValue("room", roomId);
        var id = await command.ExecuteScalarAsync(cancellationToken) as string;
        return id is null ? null : await ReadEndOperationAsync(
            connection, null, tenantId, gameId, id, cancellationToken);
    }

    /// <summary>
    /// Records the next phase of the end sequence on the operation row (FIX-END-01): the
    /// ladder advances one step at a time under the recorded business id — a skip ahead, a
    /// step back or a phase the operation never held refuses, so a late or reordered step
    /// cannot rewrite a newer operation's progress. The error column records the last
    /// failure the phase produced without leaving the phase. The release transition
    /// (FIX-SETTLE-05) closes the block episode in the same transaction: the dwell since
    /// blocked_since joins the cumulative total and the reason clears — the total survives
    /// the release, the reason only matters while the room is blocked.
    /// </summary>
    public async Task<EndOperation> AdvanceEndOperationPhaseAsync(string tenantId, string gameId,
        string endOperationId, string fromPhase, string toPhase, string? lastError = null,
        CancellationToken cancellationToken = default)
    {
        if (!EndOperationPhases.IsNext(fromPhase, toPhase))
            throw new EndOperationConflictException(
                $"The end sequence does not advance from {fromPhase} to {toPhase}.");
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            UPDATE control.end_operation
            SET phase=@to, last_error=@error, updated_at=clock_timestamp(),
                blocked_total_ms = blocked_total_ms +
                    COALESCE((EXTRACT(EPOCH FROM (clock_timestamp()-blocked_since))*1000)::bigint, 0),
                blocked_since = NULL, blocked_reason = NULL
            WHERE tenant_id=@tenant AND game_id=@game AND end_operation_id=@operation AND phase=@from
            RETURNING room_id
            """, connection);
        command.Parameters.AddWithValue("tenant", tenantId);
        command.Parameters.AddWithValue("game", gameId);
        command.Parameters.AddWithValue("operation", endOperationId);
        command.Parameters.AddWithValue("from", fromPhase);
        command.Parameters.AddWithValue("to", toPhase);
        command.Parameters.AddWithValue("error", lastError is null ? DBNull.Value : lastError);
        if (await command.ExecuteScalarAsync(cancellationToken) is null)
            throw new EndOperationConflictException(
                $"The end operation {endOperationId} is not at the phase {fromPhase}.");
        return (await ReadEndOperationAsync(
            connection, null, tenantId, gameId, endOperationId, cancellationToken))!;
    }

    /// <summary>
    /// Records the release block (FIX-SETTLE-05): the data plane's finalize transaction
    /// refused because evidence is still gathering — undisposed inputs, an incomplete
    /// manifest, a platform precondition — and the refusal's reason becomes the queryable
    /// block. The first refusal of an episode sets blocked_since; later refusals of the
    /// same episode keep it and refresh the reason, so the dwell measures the whole wait.
    /// The write only lands while the row still stands at the release phase: a pass that
    /// already released the room must not have a stale refusal paint a block onto it.
    /// </summary>
    public async Task RecordEndOperationBlockAsync(string tenantId, string gameId,
        string endOperationId, string reason, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            UPDATE control.end_operation
            SET blocked_reason=@reason,
                blocked_since=COALESCE(blocked_since, clock_timestamp()),
                last_error=@reason,
                updated_at=clock_timestamp()
            WHERE tenant_id=@tenant AND game_id=@game AND end_operation_id=@operation
                AND phase=@releasePhase
            """, connection);
        command.Parameters.AddWithValue("tenant", tenantId);
        command.Parameters.AddWithValue("game", gameId);
        command.Parameters.AddWithValue("operation", endOperationId);
        command.Parameters.AddWithValue("reason", reason);
        command.Parameters.AddWithValue("releasePhase", EndOperationPhases.PlatformClosed);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Records a failure on the operation row without leaving the phase — the
    /// restart reads the phase and the reason and retries the same step.</summary>
    public async Task RecordEndOperationErrorAsync(string tenantId, string gameId,
        string endOperationId, string lastError, CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            UPDATE control.end_operation
            SET last_error=@error, updated_at=clock_timestamp()
            WHERE tenant_id=@tenant AND game_id=@game AND end_operation_id=@operation
            """, connection);
        command.Parameters.AddWithValue("tenant", tenantId);
        command.Parameters.AddWithValue("game", gameId);
        command.Parameters.AddWithValue("operation", endOperationId);
        command.Parameters.AddWithValue("error", lastError);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>The open end operations a restarted control plane continues — every phase
    /// the sequence has not finished, oldest first.</summary>
    public async Task<List<EndOperation>> ListOpenEndOperationsAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        var ids = new List<(string Tenant, string Game, string Id)>();
        await using (var command = new NpgsqlCommand("""
            SELECT tenant_id,game_id,end_operation_id FROM control.end_operation
            WHERE phase<>@done ORDER BY created_at LIMIT 50
            """, connection))
        {
            command.Parameters.AddWithValue("done", EndOperationPhases.Done);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                ids.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        }
        var open = new List<EndOperation>();
        foreach (var (tenant, game, id) in ids)
            open.Add((await ReadEndOperationAsync(
                connection, null, tenant, game, id, cancellationToken))!);
        return open;
    }

    /// <summary>
    /// Persists the stop-delivery command and its responder set (FIX-END-05, architecture 6.4
    /// step 3). The CloseRouteVersion is derived here from the operation's own recorded facts —
    /// never taken from a caller — and frozen with the row: a repeated issue replays the
    /// recorded command, so a late or racing delivery cannot close a newer round. The responder
    /// set is every gateway that held a confirmed routing version at issue: an offline gateway
    /// keeps its process and confirm rows, so it stays a pending responder — the command waits
    /// for it, it is never skipped.
    /// </summary>
    public async Task<(StopDeliveryCommand Command, bool Duplicate, List<StopDeliveryResponder> Responders)>
        IssueStopDeliveryAsync(string tenantId, string gameId, string endOperationId,
            CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var operation = await ReadEndOperationAsync(
            connection, transaction, tenantId, gameId, endOperationId, cancellationToken)
            ?? throw new EndOperationConflictException(
                $"The end operation {endOperationId} does not exist.");
        if (operation.Phase == EndOperationPhases.Done)
            throw new EndOperationConflictException(
                $"The end operation {endOperationId} is Done; no stop delivery is issued.");

        // The replay path: a command row already stands — the recorded version is the command,
        // whatever the world looks like now.
        var existing = await ReadStopDeliveryAsync(connection, transaction, tenantId, gameId,
            endOperationId, cancellationToken);
        if (existing is not null)
        {
            var responders = await ListStopDeliveryRespondersAsync(
                connection, transaction, tenantId, gameId, endOperationId, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return (existing, true, responders);
        }

        var version = DeriveCloseRouteVersion(operation);
        await using (var insert = new NpgsqlCommand("""
            INSERT INTO control.stop_delivery(tenant_id,game_id,end_operation_id,room_id,
                round_generation,close_route_version)
            VALUES (@tenant,@game,@operation,@room,@generation,@version)
            ON CONFLICT (tenant_id,game_id,end_operation_id) DO NOTHING
            """, connection, transaction))
        {
            insert.Parameters.AddWithValue("tenant", tenantId);
            insert.Parameters.AddWithValue("game", gameId);
            insert.Parameters.AddWithValue("operation", endOperationId);
            insert.Parameters.AddWithValue("room", operation.RoomId);
            insert.Parameters.AddWithValue("generation", operation.RoundGeneration);
            insert.Parameters.AddWithValue("version", version);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
        // The responder set at issue: every platform-gateway process with a confirmed routing
        // version. No liveness filter — an offline gateway's confirm row is still its route
        // authorization, and the command must reach it whenever it comes back.
        await using (var responders = new NpgsqlCommand("""
            INSERT INTO control.stop_delivery_responder(tenant_id,game_id,end_operation_id,
                process_instance_id,endpoint)
            SELECT @tenant,@game,@operation,p.process_instance_id,p.endpoint
            FROM control.process p
            JOIN control.deployment d ON d.slot_id=p.slot_id
            JOIN control.routing_confirm c ON c.process_instance_id=p.process_instance_id
            WHERE d.role='platform-gateway'
            ON CONFLICT (tenant_id,game_id,end_operation_id,process_instance_id) DO NOTHING
            """, connection, transaction))
        {
            responders.Parameters.AddWithValue("tenant", tenantId);
            responders.Parameters.AddWithValue("game", gameId);
            responders.Parameters.AddWithValue("operation", endOperationId);
            await responders.ExecuteNonQueryAsync(cancellationToken);
        }

        var command = await ReadStopDeliveryAsync(connection, transaction, tenantId, gameId,
            endOperationId, cancellationToken)
            ?? throw new InvalidOperationException("The stop-delivery row vanished under issue.");
        var set = await ListStopDeliveryRespondersAsync(
            connection, transaction, tenantId, gameId, endOperationId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return (command, false, set);
    }

    /// <summary>The recorded stop-delivery command, or null when none was issued yet.</summary>
    public async Task<StopDeliveryCommand?> GetStopDeliveryAsync(string tenantId, string gameId,
        string endOperationId, CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        return await ReadStopDeliveryAsync(connection, null, tenantId, gameId, endOperationId,
            cancellationToken);
    }

    /// <summary>The command's responder set — the pending and answered gateways alike.</summary>
    public async Task<List<StopDeliveryResponder>> ListStopDeliveryRespondersAsync(
        string tenantId, string gameId, string endOperationId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        return await ListStopDeliveryRespondersAsync(connection, null, tenantId, gameId,
            endOperationId, cancellationToken);
    }

    /// <summary>
    /// The step-4 barrier the coordinator drives every pass (FIX-END-08, architecture 6.4
    /// step 4): the confirmation owed is every gateway holding route authorization NOW, not
    /// only the set the issue froze. One transaction grows the responder set to cover holders
    /// that authorized after the issue (a missed instance must not slip past the barrier),
    /// refreshes the endpoint of every still-pending row from the live process row (a
    /// restarted holder under the same instance id gets its current address), and returns the
    /// set with each responder's authorization currency: a holder counts while its process
    /// reports a live lifecycle within the stale window. A responder whose authorization
    /// lapsed — the process died, the supervisor reported its exit, or its reports simply
    /// stopped — owes nothing: the receiver-side fence (the standing end operation this
    /// method's caller drives) refuses its pushes regardless, so the exit is safe only
    /// because the fence stands, which is why this method refuses an operation that is not
    /// open. Rows are never rewritten to an exited state: a lapsed responder's Pending row
    /// is the honest evidence of the confirmation that never came, and a holder that comes
    /// back fresh re-enters the barrier and owes its answer again.
    /// </summary>
    public async Task<List<StopDeliveryResponder>> ResolveDeliveryStopBarrierAsync(
        string tenantId, string gameId, string endOperationId, int staleSeconds,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var open = new NpgsqlCommand(
            "SELECT phase FROM control.end_operation" +
            " WHERE tenant_id=@tenant AND game_id=@game AND end_operation_id=@operation",
            connection, transaction))
        {
            open.Parameters.AddWithValue("tenant", tenantId);
            open.Parameters.AddWithValue("game", gameId);
            open.Parameters.AddWithValue("operation", endOperationId);
            if (await open.ExecuteScalarAsync(cancellationToken) is not string phase
                || phase == EndOperationPhases.Done)
                throw new EndOperationConflictException(
                    $"The end operation {endOperationId} does not stand open for a delivery-stop barrier.");
        }
        // Holders that authorized after the issue join the standing barrier: the same
        // derivation the issue froze, inserted as pending responders so the re-push leg can
        // carry them the command and their ack is admissible.
        await using (var grow = new NpgsqlCommand("""
            INSERT INTO control.stop_delivery_responder(tenant_id,game_id,end_operation_id,
                process_instance_id,endpoint)
            SELECT @tenant,@game,@operation,p.process_instance_id,p.endpoint
            FROM control.process p
            JOIN control.deployment d ON d.slot_id=p.slot_id
            JOIN control.routing_confirm c ON c.process_instance_id=p.process_instance_id
            WHERE d.role='platform-gateway'
            ON CONFLICT (tenant_id,game_id,end_operation_id,process_instance_id) DO NOTHING
            """, connection, transaction))
        {
            grow.Parameters.AddWithValue("tenant", tenantId);
            grow.Parameters.AddWithValue("game", gameId);
            grow.Parameters.AddWithValue("operation", endOperationId);
            await grow.ExecuteNonQueryAsync(cancellationToken);
        }
        // A still-pending responder's endpoint follows its live process row — the address the
        // re-push uses, never the one the issue froze.
        await using (var refresh = new NpgsqlCommand("""
            UPDATE control.stop_delivery_responder r
            SET endpoint = p.endpoint
            FROM control.process p
            WHERE r.tenant_id=@tenant AND r.game_id=@game AND r.end_operation_id=@operation
              AND r.process_instance_id=p.process_instance_id AND r.state='Pending'
              AND p.endpoint IS NOT NULL AND r.endpoint IS DISTINCT FROM p.endpoint
            """, connection, transaction))
        {
            refresh.Parameters.AddWithValue("tenant", tenantId);
            refresh.Parameters.AddWithValue("game", gameId);
            refresh.Parameters.AddWithValue("operation", endOperationId);
            await refresh.ExecuteNonQueryAsync(cancellationToken);
        }
        var set = await ListStopDeliveryRespondersAsync(
            connection, transaction, tenantId, gameId, endOperationId, cancellationToken);
        await using (var currency = new NpgsqlCommand("""
            SELECT r.process_instance_id,
                (p.lifecycle IN ('Running','Draining')
                    AND p.observed_at >= clock_timestamp() - @stale * interval '1 second')
            FROM control.stop_delivery_responder r
            LEFT JOIN control.process p ON p.process_instance_id = r.process_instance_id
            WHERE r.tenant_id=@tenant AND r.game_id=@game AND r.end_operation_id=@operation
            """, connection, transaction))
        {
            currency.Parameters.AddWithValue("tenant", tenantId);
            currency.Parameters.AddWithValue("game", gameId);
            currency.Parameters.AddWithValue("operation", endOperationId);
            currency.Parameters.AddWithValue("stale", staleSeconds);
            await using var reader = await currency.ExecuteReaderAsync(cancellationToken);
            var flags = new Dictionary<string, bool>();
            while (await reader.ReadAsync(cancellationToken))
                flags[reader.GetString(0)] = reader.GetBoolean(1);
            set = set.Select(responder => responder with
            {
                HoldsAuthorization = flags.GetValueOrDefault(responder.ProcessInstanceId)
            }).ToList();
        }
        await transaction.CommitAsync(cancellationToken);
        return set;
    }

    /// <summary>
    /// Advances StopDeliveryIssued to DeliveryStopped only when the barrier is complete
    /// (FIX-END-08): no gateway holding route authorization — a live process with a confirmed
    /// routing version — stands without its own Stopped confirmation. The completeness
    /// predicate lives in the advancing statement itself, so a holder whose confirm lands
    /// between the coordinator's snapshot and this update cannot slip past: the update's own
    /// snapshot sees it or the update commits before the confirm, and a holder that
    /// postdates the barrier provably never pushed for this round (its monitor answers no
    /// resolve until its confirm lands, and the receiver-side fence refuses the closed round
    /// from then on). An empty holder set is vacuously complete. False means the barrier
    /// parked — or another driver moved the phase first — and the next pass reads the row
    /// exactly as it stands.
    /// </summary>
    public async Task<bool> AdvanceDeliveryStoppedBarrierAsync(string tenantId, string gameId,
        string endOperationId, int staleSeconds, CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            UPDATE control.end_operation
            SET phase='DeliveryStopped', last_error=NULL, updated_at=clock_timestamp()
            WHERE tenant_id=@tenant AND game_id=@game AND end_operation_id=@operation
              AND phase='StopDeliveryIssued'
              AND NOT EXISTS (
                SELECT 1 FROM control.process p
                JOIN control.deployment d ON d.slot_id=p.slot_id
                JOIN control.routing_confirm c ON c.process_instance_id=p.process_instance_id
                WHERE d.role='platform-gateway'
                  AND p.lifecycle IN ('Running','Draining')
                  AND p.observed_at >= clock_timestamp() - @stale * interval '1 second'
                  AND NOT EXISTS (
                    SELECT 1 FROM control.stop_delivery_responder r
                    WHERE r.tenant_id=@tenant AND r.game_id=@game AND r.end_operation_id=@operation
                      AND r.process_instance_id=p.process_instance_id AND r.state='Stopped'))
            """, connection);
        command.Parameters.AddWithValue("tenant", tenantId);
        command.Parameters.AddWithValue("game", gameId);
        command.Parameters.AddWithValue("operation", endOperationId);
        command.Parameters.AddWithValue("stale", staleSeconds);
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    /// <summary>
    /// Records one responder's DeliveryStopped confirmation (FIX-END-07, architecture 6.4
    /// step 4). The ack validates against the recorded command — never the phase — so a
    /// restarted gateway re-acking after the ladder moved past DeliveryStopped still
    /// replays idempotently. First answer wins: only a Pending row accepts the update, so
    /// a repeat at the recorded version changes nothing and the first answer's watermark
    /// and counts stand — a restarted process's honest zeros cannot lower them. A wrong
    /// room or version, an instance outside the frozen responder set, or a command that
    /// never issued refuses without writing.
    /// </summary>
    public async Task<DeliveryStopAck> AcknowledgeStopDeliveryAsync(string tenantId, string gameId,
        string endOperationId, string roomId, string processInstanceId, long closeRouteVersion,
        long cutoffSequence, long drainedOutOfRound, long drainedRouted,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        var command = await ReadStopDeliveryAsync(connection, null, tenantId, gameId,
            endOperationId, cancellationToken)
            ?? throw new EndOperationConflictException(
                $"The end operation {endOperationId} never issued a stop-delivery command.");
        if (command.RoomId != roomId)
            throw new EndOperationConflictException(
                $"The stop-delivery command for {endOperationId} closes the room {command.RoomId}, not {roomId}.");
        if (command.CloseRouteVersion != closeRouteVersion)
            throw new EndOperationConflictException(
                $"The stop-delivery command for {roomId} stands at version {command.CloseRouteVersion}; " +
                $"the ack names {closeRouteVersion}.");
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var update = new NpgsqlCommand("""
            UPDATE control.stop_delivery_responder
            SET state='Stopped', answered_at=clock_timestamp(),
                cutoff_sequence=@cutoff, drained_out_of_round=@outOfRound, drained_routed=@routed
            WHERE tenant_id=@tenant AND game_id=@game AND end_operation_id=@operation
              AND process_instance_id=@instance AND state='Pending'
            """, connection, transaction))
        {
            update.Parameters.AddWithValue("tenant", tenantId);
            update.Parameters.AddWithValue("game", gameId);
            update.Parameters.AddWithValue("operation", endOperationId);
            update.Parameters.AddWithValue("instance", processInstanceId);
            update.Parameters.AddWithValue("cutoff", cutoffSequence);
            update.Parameters.AddWithValue("outOfRound", drainedOutOfRound);
            update.Parameters.AddWithValue("routed", drainedRouted);
            if (await update.ExecuteNonQueryAsync(cancellationToken) > 0)
            {
                await transaction.CommitAsync(cancellationToken);
                return new DeliveryStopAck(true, false, command.CloseRouteVersion);
            }
        }
        // Zero rows: the responder either already answered at this version — the recorded
        // answer is the answer — or holds no row at all.
        var responder = (await ListStopDeliveryRespondersAsync(connection, transaction, tenantId, gameId,
                endOperationId, cancellationToken))
            .SingleOrDefault(row => row.ProcessInstanceId == processInstanceId);
        if (responder is null)
        {
            // FIX-END-08: a holder that authorized after the issue may hold no row yet — its
            // ack proves possession of the command (the room, the operation, the exact frozen
            // version), so the confirmation is counted, not refused. An instance without
            // route authorization remains a stranger.
            await using (var holder = new NpgsqlCommand("""
                SELECT p.endpoint FROM control.process p
                JOIN control.deployment d ON d.slot_id=p.slot_id
                JOIN control.routing_confirm c ON c.process_instance_id=p.process_instance_id
                WHERE d.role='platform-gateway' AND p.process_instance_id=@instance
                """, connection, transaction))
            {
                holder.Parameters.AddWithValue("instance", processInstanceId);
                var endpoint = await holder.ExecuteScalarAsync(cancellationToken) as string;
                if (endpoint is not null)
                {
                    await using (var insert = new NpgsqlCommand("""
                        INSERT INTO control.stop_delivery_responder(tenant_id,game_id,end_operation_id,
                            process_instance_id,endpoint)
                        VALUES (@tenant,@game,@operation,@instance,@endpoint)
                        ON CONFLICT (tenant_id,game_id,end_operation_id,process_instance_id) DO NOTHING
                        """, connection, transaction))
                    {
                        insert.Parameters.AddWithValue("tenant", tenantId);
                        insert.Parameters.AddWithValue("game", gameId);
                        insert.Parameters.AddWithValue("operation", endOperationId);
                        insert.Parameters.AddWithValue("instance", processInstanceId);
                        insert.Parameters.AddWithValue("endpoint", endpoint);
                        await insert.ExecuteNonQueryAsync(cancellationToken);
                    }
                    await using (var late = new NpgsqlCommand("""
                        UPDATE control.stop_delivery_responder
                        SET state='Stopped', answered_at=clock_timestamp(),
                            cutoff_sequence=@cutoff, drained_out_of_round=@outOfRound, drained_routed=@routed
                        WHERE tenant_id=@tenant AND game_id=@game AND end_operation_id=@operation
                          AND process_instance_id=@instance AND state='Pending'
                        """, connection, transaction))
                    {
                        late.Parameters.AddWithValue("tenant", tenantId);
                        late.Parameters.AddWithValue("game", gameId);
                        late.Parameters.AddWithValue("operation", endOperationId);
                        late.Parameters.AddWithValue("instance", processInstanceId);
                        late.Parameters.AddWithValue("cutoff", cutoffSequence);
                        late.Parameters.AddWithValue("outOfRound", drainedOutOfRound);
                        late.Parameters.AddWithValue("routed", drainedRouted);
                        if (await late.ExecuteNonQueryAsync(cancellationToken) > 0)
                        {
                            await transaction.CommitAsync(cancellationToken);
                            return new DeliveryStopAck(true, false, command.CloseRouteVersion);
                        }
                    }
                }
            }
            await transaction.CommitAsync(cancellationToken);
            throw new EndOperationConflictException(
                $"The process {processInstanceId} is not a stop-delivery responder for {endOperationId}.");
        }
        if (responder.State != "Stopped")
            throw new EndOperationConflictException(
                $"The responder {processInstanceId} stands at {responder.State}, which no ack records.");
        await transaction.CommitAsync(cancellationToken);
        return new DeliveryStopAck(false, true, command.CloseRouteVersion);
    }

    private static async Task<StopDeliveryCommand?> ReadStopDeliveryAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, string tenantId, string gameId,
        string endOperationId, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT room_id,round_generation,close_route_version,issued_at
            FROM control.stop_delivery
            WHERE tenant_id=@tenant AND game_id=@game AND end_operation_id=@operation
            """, connection, transaction);
        command.Parameters.AddWithValue("tenant", tenantId);
        command.Parameters.AddWithValue("game", gameId);
        command.Parameters.AddWithValue("operation", endOperationId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return new StopDeliveryCommand(tenantId, gameId, endOperationId, reader.GetString(0),
            reader.GetInt64(1), reader.GetInt64(2), reader.GetDateTime(3));
    }

    private static async Task<List<StopDeliveryResponder>> ListStopDeliveryRespondersAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, string tenantId, string gameId,
        string endOperationId, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT process_instance_id,endpoint,state,answered_at,
                cutoff_sequence,drained_out_of_round,drained_routed
            FROM control.stop_delivery_responder
            WHERE tenant_id=@tenant AND game_id=@game AND end_operation_id=@operation
            ORDER BY process_instance_id
            """, connection, transaction);
        command.Parameters.AddWithValue("tenant", tenantId);
        command.Parameters.AddWithValue("game", gameId);
        command.Parameters.AddWithValue("operation", endOperationId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var responders = new List<StopDeliveryResponder>();
        while (await reader.ReadAsync(cancellationToken))
            responders.Add(new StopDeliveryResponder(reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1), reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetDateTime(3),
                reader.GetInt64(4), reader.GetInt64(5), reader.GetInt64(6)));
        return responders;
    }

    /// <summary>The close version is derived from the operation's own recorded facts — the
    /// room, its round generation, and the mount generations the begin froze. The same
    /// operation always derives the same version; a different room or a newer round derives a
    /// different one, so a replayed command can never close a newer round.</summary>
    private static long DeriveCloseRouteVersion(EndOperation operation) =>
        ContentHash(new[] { $"room|{operation.RoomId}|{operation.RoundGeneration}" }
            .Concat(operation.BindingGenerations
                .OrderBy(binding => binding.BindingId)
                .Select(binding =>
                    $"binding|{binding.BindingId}|{binding.Generation}|{binding.RoundGeneration}")));

    /// <summary>
    /// Registers a mount's route source (FIX-END-02, architecture 6.4): the StreamerServer
    /// reports the binding right after the mount commits, and the control plane derives the
    /// platform identity from the mount's live session — a session row exists only because a
    /// platform-verified login created it, so an unverified identity establishes no route.
    /// The report must name the mount's current generation; the row is keyed per streamer and
    /// game, and a report naming an older generation than the recorded row never overwrites
    /// it — late evidence loses, always. A replay of the recorded generation answers the one
    /// row as Duplicate.
    /// </summary>
    public async Task<(RouteSourceRecord Record, bool Duplicate)> RegisterRouteSourceAsync(
        string tenantId, string gameId, string streamerId, string bindingId, long bindingGeneration,
        CancellationToken cancellationToken = default)
    {
        if (bindingGeneration <= 0) throw new ArgumentOutOfRangeException(nameof(bindingGeneration));
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        // The verified join is the whole fence: the binding must exist, belong to this
        // streamer's tenant and game, and sit on a live session — whose platform identity is
        // what gets recorded, never anything the report claims.
        string platform, canonicalLiveSessionKey, roomId;
        await using (var mount = new NpgsqlCommand("""
            SELECT b.tenant_id,b.game_id,b.generation,b.current_room_id,
                s.streamer_id,s.platform,s.canonical_live_session_key
            FROM live.binding b
            JOIN live.session s ON s.live_session_id=b.live_session_id
            WHERE b.binding_id=@binding
            """, connection))
        {
            mount.Parameters.AddWithValue("binding", bindingId);
            await using var reader = await mount.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                throw new RouteSourceUnverifiedException(
                    $"The binding {bindingId} does not exist; no route source is established.");
            if (reader.GetString(0) != tenantId || reader.GetString(1) != gameId ||
                reader.GetString(4) != streamerId)
                throw new RouteSourceUnverifiedException(
                    $"The binding {bindingId} does not belong to {tenantId}/{gameId}/{streamerId}.");
            if (reader.GetInt64(2) != bindingGeneration)
                throw new StaleBindingGenerationException(
                    $"The binding {bindingId} holds generation {reader.GetInt64(2)}, not " +
                    $"{bindingGeneration}; the report is late or ahead of the mount.");
            platform = reader.GetString(5);
            canonicalLiveSessionKey = reader.GetString(6);
            roomId = reader.IsDBNull(3) ? "" : reader.GetString(3);
        }

        // The recorded row first: the newer generation wins by refusing the older report,
        // and the recorded generation replays as the one row.
        var existingGeneration = -1L;
        var existingBinding = "";
        await using (var existing = new NpgsqlCommand("""
            SELECT binding_id,binding_generation FROM control.route_source
            WHERE tenant_id=@tenant AND game_id=@game AND streamer_id=@streamer
            """, connection))
        {
            existing.Parameters.AddWithValue("tenant", tenantId);
            existing.Parameters.AddWithValue("game", gameId);
            existing.Parameters.AddWithValue("streamer", streamerId);
            await using var reader = await existing.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                existingBinding = reader.GetString(0);
                existingGeneration = reader.GetInt64(1);
            }
        }
        if (existingGeneration > bindingGeneration)
            throw new StaleBindingGenerationException(
                $"The route source for {streamerId} already holds generation {existingGeneration}; " +
                $"generation {bindingGeneration} does not overwrite it.");
        var duplicate = existingGeneration == bindingGeneration && existingBinding == bindingId;
        if (!duplicate)
        {
            // The upsert's WHERE keeps a concurrent older report from overwriting a newer
            // registration between the checks above and this write.
            await using var upsert = new NpgsqlCommand("""
                INSERT INTO control.route_source(tenant_id,game_id,streamer_id,binding_id,
                    platform,canonical_live_session_key,binding_generation,room_id)
                VALUES (@tenant,@game,@streamer,@binding,@platform,@canonical,@generation,@room)
                ON CONFLICT (tenant_id,game_id,streamer_id) DO UPDATE SET
                    binding_id=@binding,platform=@platform,
                    canonical_live_session_key=@canonical,binding_generation=@generation,
                    room_id=@room,updated_at=clock_timestamp()
                WHERE control.route_source.binding_generation < @generation
                """, connection);
            upsert.Parameters.AddWithValue("tenant", tenantId);
            upsert.Parameters.AddWithValue("game", gameId);
            upsert.Parameters.AddWithValue("streamer", streamerId);
            upsert.Parameters.AddWithValue("binding", bindingId);
            upsert.Parameters.AddWithValue("platform", platform);
            upsert.Parameters.AddWithValue("canonical", canonicalLiveSessionKey);
            upsert.Parameters.AddWithValue("generation", bindingGeneration);
            upsert.Parameters.AddWithValue("room", roomId);
            if (await upsert.ExecuteNonQueryAsync(cancellationToken) == 0)
                throw new StaleBindingGenerationException(
                    $"A newer route source for {streamerId} won the registration race.");
        }

        // The row as it stands now — replay and fresh write alike.
        await using (var read = new NpgsqlCommand("""
            SELECT binding_id,platform,canonical_live_session_key,binding_generation,room_id,
                registered_at,updated_at
            FROM control.route_source
            WHERE tenant_id=@tenant AND game_id=@game AND streamer_id=@streamer
            """, connection))
        {
            read.Parameters.AddWithValue("tenant", tenantId);
            read.Parameters.AddWithValue("game", gameId);
            read.Parameters.AddWithValue("streamer", streamerId);
            await using var reader = await read.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                throw new InvalidOperationException("The route source row vanished under registration.");
            var record = new RouteSourceRecord(tenantId, gameId, streamerId, reader.GetString(0),
                reader.GetString(1), reader.GetString(2), reader.GetInt64(3), reader.GetString(4),
                reader.GetDateTime(5), reader.GetDateTime(6));
            return (record, duplicate && record.BindingGeneration == bindingGeneration);
        }
    }

    /// <summary>
    /// The match-intent gate (FIX-LINK-03, architecture 6.4): a mount's intent is accepted only
    /// while the mount is the registered route source — the row exists for the binding and still
    /// carries the mount's current generation. An unregistered mount establishes no intent, and a
    /// stale generation means the mount was superseded. The streamer is the row's, never a caller
    /// field — the intent names the mount (binding + generation) and the control plane joins it to
    /// the verified identity.
    /// </summary>
    public async Task<RouteSourceRecord> ValidateMatchIntentSourceAsync(
        string tenantId, string gameId, string bindingId, long bindingGeneration,
        CancellationToken cancellationToken = default)
    {
        if (bindingGeneration <= 0) throw new ArgumentOutOfRangeException(nameof(bindingGeneration));
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        // binding_id is globally unique (a binding belongs to one streamer), so the row is the
        // one source for this mount; the recorded generation is the currency check.
        await using var read = new NpgsqlCommand("""
            SELECT streamer_id,binding_id,binding_generation,platform,canonical_live_session_key,
                room_id,registered_at,updated_at
            FROM control.route_source
            WHERE tenant_id=@tenant AND game_id=@game AND binding_id=@binding
            """, connection);
        read.Parameters.AddWithValue("tenant", tenantId);
        read.Parameters.AddWithValue("game", gameId);
        read.Parameters.AddWithValue("binding", bindingId);
        await using var reader = await read.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw new RouteSourceUnverifiedException(
                $"The binding {bindingId} is not a registered route source for {tenantId}/{gameId}; " +
                "the mount establishes no match intent.");
        var streamerId = reader.GetString(0);
        var recordedGeneration = reader.GetInt64(2);
        if (recordedGeneration != bindingGeneration)
            throw new StaleBindingGenerationException(
                $"The route source for {streamerId} holds generation {recordedGeneration}, not " +
                $"{bindingGeneration}; the intent names a superseded mount.");
        return new RouteSourceRecord(tenantId, gameId, streamerId, reader.GetString(1),
            reader.GetString(3), reader.GetString(4), recordedGeneration,
            reader.IsDBNull(5) ? "" : reader.GetString(5), reader.GetDateTime(6),
            reader.GetDateTime(7));
    }

    /// <summary>
    /// Claims a batch of round.started outbox events for dispatch (FIX-LINK-04): a pending
    /// event is taken with a lease — the claim is atomic, and an event stranded in Dispatching
    /// (the control plane died between claim and completion) is reclaimable once its lease
    /// lapses. The dispatch is exactly the event's durable record: only a claimed event is
    /// delivered, and only a completed one is retired.
    /// </summary>
    public async Task<IReadOnlyList<RoundStartedEvent>> ClaimRoundStartedAsync(
        int batchSize, CancellationToken cancellationToken = default)
    {
        if (batchSize <= 0) throw new ArgumentOutOfRangeException(nameof(batchSize));
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var list = new NpgsqlCommand("""
            SELECT effect_id,payload
            FROM infra.outbox
            WHERE kind='round.started'
              AND (status='Pending' OR (status='Dispatching' AND next_attempt < clock_timestamp()))
            ORDER BY committed_at
            LIMIT @batch
            """, connection);
        list.Parameters.AddWithValue("batch", batchSize);
        var claimed = new List<RoundStartedEvent>();
        await using (var reader = await list.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var effectId = reader.GetString(0);
                var payload = JsonSerializer.Deserialize<RoundStartedPayload>(reader.GetString(1));
                if (payload is null) continue;
                claimed.Add(new RoundStartedEvent(effectId, payload.RoomId, payload.TenantId,
                    payload.GameId, payload.Epoch, payload.Participants.Select(participant =>
                        new RoundStartedParticipant(participant.BindingId, participant.Generation)).ToList()));
            }
        }
        foreach (var eventEntry in claimed.ToArray())
        {
            await using var claim = new NpgsqlCommand("""
                UPDATE infra.outbox
                SET status='Dispatching', next_attempt=clock_timestamp()+interval '30 seconds',
                    attempt_count=attempt_count+1
                WHERE effect_id=@effect
                  AND (status='Pending' OR (status='Dispatching' AND next_attempt < clock_timestamp()))
                """, connection);
            claim.Parameters.AddWithValue("effect", eventEntry.EffectId);
            if (await claim.ExecuteNonQueryAsync(cancellationToken) == 0)
            {
                // Another dispatcher took it (or the lease moved); the list was a snapshot.
                claimed.RemoveAll(entry => entry.EffectId == eventEntry.EffectId);
            }
        }
        return claimed;
    }

    /// <summary>Retires a claimed event (success → Done) or returns it to the pending queue
    /// (failure → Pending, next round retries). The dispatcher calls exactly one of these per
    /// claimed event; the lease makes a lost completion retryable, never lost.</summary>
    public async Task CompleteRoundStartedAsync(string effectId, bool delivered,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            UPDATE infra.outbox
            SET status=@status, next_attempt=NULL
            WHERE effect_id=@effect
            """, connection);
        command.Parameters.AddWithValue("status", delivered ? "Done" : "Pending");
        command.Parameters.AddWithValue("effect", effectId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Resolves a claimed round.started event's dispatch targets (FIX-LINK-04): the durable
    /// participant set of the room (game.participation, the authority the commit wrote) joined
    /// to each mount's streamer, its mount generation, and the owning StreamerServer instance
    /// and endpoint from the process reports. A participant whose mount no longer points at the
    /// room is moot — the round released it before the dispatch; the caller skips it rather
    /// than deliver stale evidence. Endpoint stays null while the owner is unknown (retry).
    /// </summary>
    public async Task<IReadOnlyList<ParticipationTarget>> ResolveParticipationTargetsAsync(
        string roomId, CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT s.streamer_id,b.binding_id,b.generation,b.round_generation,b.current_room_id,
                o.owner_process_instance_id,pr.endpoint
            FROM game.participation p
            JOIN live.binding b ON b.binding_id=p.binding_id
            JOIN live.session s ON s.live_session_id=b.live_session_id
            LEFT JOIN control.ownership o ON o.kind='streamer' AND o.tenant_id=b.tenant_id
                AND o.game_id=b.game_id AND o.entity_id=s.streamer_id
            LEFT JOIN control.process pr ON pr.process_instance_id=o.owner_process_instance_id
            WHERE p.room_id=@room AND p.state='Active'
            ORDER BY s.streamer_id,b.binding_id
            """, connection);
        command.Parameters.AddWithValue("room", roomId);
        var targets = new List<ParticipationTarget>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var bindingId = reader.GetString(1);
            var roundGeneration = reader.GetInt64(3);
            // A mount whose pointer moved away was released by the round's terminal path — the
            // commit no longer describes this streamer, so the event is moot for it.
            if (!StringComparer.Ordinal.Equals(reader.IsDBNull(4) ? null : reader.GetString(4), roomId))
                continue;
            targets.Add(new ParticipationTarget(
                reader.GetString(0), bindingId, reader.GetInt64(2), roundGeneration,
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6)));
        }
        return targets;
    }

    /// <summary>The round.started payload exactly as the commit wrote it: PascalCase
    /// anonymous-shape properties and participants as anonymous objects (BindingId, Generation —
    /// the binding id and the round generation it advanced to). The row was write-only until this
    /// dispatcher — this is the first consumer, so the wire shape is parsed verbatim and mapped.</summary>
    private sealed record RoundStartedPayload(
        string RoomId, string TenantId, string GameId, long Epoch,
        List<RoundStartedParticipantPayload> Participants);

    private sealed record RoundStartedParticipantPayload(string BindingId, long Generation);

    /// <summary>
    /// Records a PlatformGateway's installed routing version (FIX-END-03): the gateway
    /// confirms the exact version it answers resolves from, and until that confirm lands the
    /// gateway's own face refuses to push — an unconfirmed picture guesses no rooms. The
    /// confirm is keyed by the gateway's process identity from its certificate, so no caller
    /// can confirm on another instance's behalf.
    /// </summary>
    public async Task ConfirmRoutingVersionAsync(string processInstanceId, long version,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            INSERT INTO control.routing_confirm(process_instance_id,version)
            VALUES (@instance,@version)
            ON CONFLICT (process_instance_id) DO UPDATE SET
                version=@version,confirmed_at=clock_timestamp()
            """, connection);
        command.Parameters.AddWithValue("instance", processInstanceId);
        command.Parameters.AddWithValue("version", version);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>The version a gateway last confirmed, or null when it never did.</summary>
    public async Task<long?> ReadRoutingConfirmAsync(string processInstanceId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            "SELECT version FROM control.routing_confirm WHERE process_instance_id=@instance",
            connection);
        command.Parameters.AddWithValue("instance", processInstanceId);
        return (long?)await command.ExecuteScalarAsync(cancellationToken);
    }

    private static async Task<EndOperation?> ReadEndOperationAsync(NpgsqlConnection connection,
        NpgsqlTransaction? transaction, string tenantId, string gameId, string endOperationId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT room_id,round_generation,binding_generations,final_tick,result_version,
                phase,last_error,created_at,updated_at,
                blocked_reason,blocked_since,blocked_total_ms
            FROM control.end_operation
            WHERE tenant_id=@tenant AND game_id=@game AND end_operation_id=@operation
            """, connection, transaction);
        command.Parameters.AddWithValue("tenant", tenantId);
        command.Parameters.AddWithValue("game", gameId);
        command.Parameters.AddWithValue("operation", endOperationId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        var generations = new List<EndMountGeneration>();
        using (var document = JsonDocument.Parse(reader.GetString(2)))
            foreach (var entry in document.RootElement.EnumerateArray())
                generations.Add(new EndMountGeneration(
                    entry.GetProperty("bindingId").GetString()!,
                    entry.GetProperty("generation").GetInt64(),
                    entry.GetProperty("roundGeneration").GetInt64()));
        return new EndOperation(tenantId, gameId, endOperationId, reader.GetString(0),
            reader.GetInt64(1), generations, reader.GetInt64(3), reader.GetInt64(4),
            reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.GetDateTime(7), reader.GetDateTime(8),
            reader.IsDBNull(9) ? null : reader.GetString(9),
            reader.IsDBNull(10) ? null : reader.GetDateTime(10),
            reader.GetInt64(11));
    }
}

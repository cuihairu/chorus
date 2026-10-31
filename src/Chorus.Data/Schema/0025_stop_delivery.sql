-- FIX-END-05: the persisted stop-delivery command (architecture 6.4 step 3). One row per end
-- operation records the command exactly as issued: the room, the round generation the begin
-- derived from the mounts, and the CloseRouteVersion the control plane computed from the same
-- recorded facts. The version is derived at issue and never re-computed — a repeated delivery
-- of the recorded command always carries the version it froze with, so a replay can never
-- close a newer round: late evidence loses, as everywhere else in the sequence.

CREATE TABLE control.stop_delivery (
    tenant_id text NOT NULL,
    game_id text NOT NULL,
    end_operation_id text NOT NULL,
    room_id text NOT NULL,
    round_generation bigint NOT NULL,
    close_route_version bigint NOT NULL,
    issued_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    PRIMARY KEY (tenant_id, game_id, end_operation_id),
    FOREIGN KEY (tenant_id, game_id, end_operation_id)
        REFERENCES control.end_operation(tenant_id, game_id, end_operation_id)
);

-- The responder set snapshotted at issue: every gateway that held a confirmed routing version
-- when the command was recorded. Deliberately over-inclusive — an offline gateway keeps its
-- process and confirm rows (they are never deleted), so it stays a pending responder until it
-- answers; the later steps (FIX-END-07/08) drive the per-responder state to its confirmation.
CREATE TABLE control.stop_delivery_responder (
    tenant_id text NOT NULL,
    game_id text NOT NULL,
    end_operation_id text NOT NULL,
    process_instance_id text NOT NULL,
    endpoint text,
    state text NOT NULL DEFAULT 'Pending',
    answered_at timestamptz,
    PRIMARY KEY (tenant_id, game_id, end_operation_id, process_instance_id),
    FOREIGN KEY (tenant_id, game_id, end_operation_id)
        REFERENCES control.stop_delivery(tenant_id, game_id, end_operation_id)
);

-- Persistent room-recovery operations (architecture 10.3, FIX-TAKE-04). The transfer
-- transaction records the operation the same instant it fences the old owner: a lost reply
-- replays by the same operation id instead of bumping the epoch twice, and a restarted
-- control plane reads the open phase to continue where the room stands. The row is
-- progress, never authority — the ownership row remains the fence.

CREATE TABLE control.recovery_operation (
    tenant_id text NOT NULL,
    game_id text NOT NULL,
    operation_id text NOT NULL,
    room_id text NOT NULL,
    from_instance text NOT NULL,
    to_instance text NOT NULL,
    target_epoch bigint NOT NULL,
    phase text NOT NULL,
    deadline_at timestamptz NOT NULL,
    last_tick bigint NOT NULL DEFAULT 0,
    last_error text,
    created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    updated_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    PRIMARY KEY (tenant_id, game_id, operation_id)
);

CREATE INDEX ix_control_recovery_open_room
    ON control.recovery_operation(tenant_id, game_id, room_id, phase);

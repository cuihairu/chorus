-- GM room-epoch operations (architecture 4.5, FIX-GM-02). A GM epoch bump keeps the room's
-- owner, moves the ownership to Recovering and bumps BOTH the ownership and the room epoch
-- in one transaction. This row is the idempotent receipt and the query-by-operation-id record
-- a lost reply replays against (FIX-GM-05): the request shape (expected/target) is the
-- idempotency identity, the outcome (old/new epoch, owner, status) is what a retry returns.
-- The row is progress, never authority — the ownership row remains the fence. It deliberately
-- lives apart from control.recovery_operation so the takeover sweep (which scans that table's
-- open phases) never reassigns a GM-bumped room: a GM bump is recovered explicitly, not by
-- the sweep.

CREATE TABLE control.epoch_operation (
    tenant_id text NOT NULL,
    game_id text NOT NULL,
    operation_id text NOT NULL,
    room_id text NOT NULL,
    owner_instance_id text NOT NULL,
    expected_epoch bigint NOT NULL,
    target_epoch bigint NOT NULL,
    old_epoch bigint NOT NULL,
    new_epoch bigint NOT NULL,
    reason text NOT NULL,
    created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    updated_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    PRIMARY KEY (tenant_id, game_id, operation_id)
);

CREATE INDEX ix_control_epoch_operation_room
    ON control.epoch_operation(tenant_id, game_id, room_id);

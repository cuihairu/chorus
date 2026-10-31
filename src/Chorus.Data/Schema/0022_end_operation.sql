-- Persistent end operations (architecture 6.4 strict order, FIX-END-01). One operation per
-- round: the begin records the business id, the room's round generation and the mount
-- generations that entered it, and every later step records its phase and error on the same
-- row. The row is progress, never authority — ownership, routing and the settlement stores
-- stay the fences. A restarted control plane reads the open phase and continues under the
-- same business id; a late request naming a superseded generation is refused at the fence
-- and cannot touch the round the mounts have moved on to.

CREATE TABLE control.end_operation (
    tenant_id text NOT NULL,
    game_id text NOT NULL,
    end_operation_id text NOT NULL,
    room_id text NOT NULL,
    round_generation bigint NOT NULL,
    binding_generations jsonb NOT NULL,
    final_tick bigint NOT NULL DEFAULT 0,
    result_version bigint NOT NULL DEFAULT 0,
    phase text NOT NULL,
    last_error text,
    created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    updated_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    PRIMARY KEY (tenant_id, game_id, end_operation_id)
);

-- One end operation per round: a second business id for the same room refuses visibly
-- instead of racing two coordinators through the same steps.
CREATE UNIQUE INDEX ux_control_end_room ON control.end_operation(tenant_id, game_id, room_id);

-- The resume read for a restarted control plane: the open phases, oldest first.
CREATE INDEX ix_control_end_open ON control.end_operation(tenant_id, game_id, phase);

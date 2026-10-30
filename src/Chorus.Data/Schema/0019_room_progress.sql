-- FIX-TAKE-06: per-room progress, reported separately from the process readiness row.
-- A host whose heartbeats stay healthy may hold rooms whose actors stopped; this table
-- carries what each host last reported per room. progress_at is database time and only
-- moves when the reported head (state or tick) moves — a frozen tick across reports is
-- the stall signal, never fresh evidence.
CREATE TABLE control.room_progress (
    process_instance_id text NOT NULL,
    room_id text NOT NULL,
    epoch bigint NOT NULL CHECK (epoch > 0),
    state text NOT NULL,
    last_tick bigint NOT NULL DEFAULT 0 CHECK (last_tick >= 0),
    last_committed_at timestamptz,
    stop_reason text,
    report_sequence bigint NOT NULL CHECK (report_sequence >= 0),
    reported_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    progress_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    PRIMARY KEY (process_instance_id, room_id));
CREATE INDEX ix_control_room_progress_state ON control.room_progress(state);

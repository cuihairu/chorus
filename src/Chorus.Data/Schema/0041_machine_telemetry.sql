-- FIX-OBS-06: reliable machine telemetry reporting. The node agent samples the machine
-- collectors periodically and reports to the control plane, which lands each accepted
-- snapshot here. Telemetry is an append-only time series: the OBS-07 statistics API reads
-- the latest window from these rows. Per-mount disk usage rides its own child table so the
-- mount label stays a bounded dimension and a snapshot with no usable mount still records
-- the scalar families. The nullable scalar columns carry the collectors' "absent, never a
-- fake zero" contract; report_sequence is stored for diagnostics (an at-least-once retry
-- can duplicate a row, harmless for periodic levels). received_at is the control-plane
-- clock, sampled_at the node's — two clocks, kept separate.
CREATE SCHEMA IF NOT EXISTS ops;

CREATE TABLE ops.machine_sample (
    id bigserial PRIMARY KEY,
    node_id text NOT NULL,
    agent_instance_id text NOT NULL,
    sampled_at timestamptz NOT NULL,
    report_sequence bigint NOT NULL,
    cpu_percent double precision,
    memory_total_bytes bigint,
    memory_used_bytes bigint,
    memory_used_percent double precision,
    load_1m double precision,
    load_5m double precision,
    load_15m double precision,
    received_at timestamptz NOT NULL DEFAULT clock_timestamp()
);

CREATE INDEX machine_sample_node_time_idx
    ON ops.machine_sample(node_id, sampled_at DESC);

CREATE TABLE ops.machine_disk_sample (
    id bigserial PRIMARY KEY,
    machine_sample_id bigint NOT NULL REFERENCES ops.machine_sample(id) ON DELETE CASCADE,
    mount text NOT NULL,
    total_bytes bigint NOT NULL,
    used_bytes bigint NOT NULL,
    used_percent double precision NOT NULL
);

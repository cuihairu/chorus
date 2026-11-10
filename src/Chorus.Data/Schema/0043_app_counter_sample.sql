-- FIX-OBS-10: application counters reported by the faces themselves (today the platform
-- gateway's ingress decisions). Each accepted report appends one row per family: the value
-- is cumulative since the reporting process started, so a restart resets that reporter's
-- series and the statistics read face takes the latest row per reporter inside a freshness
-- window — the same latest-per-reporter rule the machine telemetry uses. family is the
-- dictionary metric name (Chorus.Rpc OpsMetrics), kept as a bounded text dimension so the
-- later OBS families (FIX-OBS-11/12) report through the same table without a migration.
-- reported_at is the reporter's clock, received_at the control plane's.
CREATE TABLE ops.app_counter_sample (
    id bigserial PRIMARY KEY,
    node_id text NOT NULL,
    process_instance_id text NOT NULL,
    family text NOT NULL,
    value bigint NOT NULL,
    reported_at timestamptz NOT NULL,
    received_at timestamptz NOT NULL DEFAULT clock_timestamp()
);

CREATE INDEX app_counter_sample_reporter_idx
    ON ops.app_counter_sample(node_id, process_instance_id, family, reported_at DESC);

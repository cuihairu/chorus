-- FIX-OBS-07: per-process telemetry. The node agent samples each supervised process
-- (CPU, resident memory, uptime) and rides the machine telemetry snapshot — each
-- accepted machine_sample row carries its process children here. process_instance_id
-- and role are the bounded dimensions the process stats API groups by; uptime comes
-- from the supervisor's recorded start time, cpu_percent is a delta over the previous
-- sample (absent on the first baseline sample). The child row is dropped with its
-- parent machine_sample (CASCADE), matching the disk child table.
CREATE TABLE ops.process_sample (
    id bigserial PRIMARY KEY,
    machine_sample_id bigint NOT NULL REFERENCES ops.machine_sample(id) ON DELETE CASCADE,
    process_instance_id text NOT NULL,
    role text NOT NULL,
    cpu_percent double precision NOT NULL,
    memory_used_bytes bigint NOT NULL,
    uptime_seconds bigint NOT NULL
);

CREATE INDEX process_sample_sample_idx
    ON ops.process_sample(machine_sample_id);

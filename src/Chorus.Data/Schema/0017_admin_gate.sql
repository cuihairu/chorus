-- Admin command idempotency and the match-queue gate (operations 6, architecture 7.1).
-- Every admin command lands once per (request_id, command) — a retried command replays its
-- reply instead of acting twice; the queue gate parks a partition's pairing without touching
-- its tickets or reservations, so resumption loses nothing.

CREATE UNIQUE INDEX ux_control_audit_request ON control.audit(request_id, command);

CREATE TABLE control.queue_gate (
    partition_key text PRIMARY KEY,
    suspended boolean NOT NULL,
    updated_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    updated_by text NOT NULL
);

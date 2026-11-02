-- FIX-SETTLE-03: the settlement executor's claim and retry state (architecture 9.3). One
-- row per room the business executor is working — the claim lease serialises concurrent
-- executors (only one holds a room at a time in the common case; an expired lease is
-- re-claimable, and the per-grant idempotent floor still bounds a rare overlap), the attempt
-- count and next attempt bound the retry, and the failure reason stays queryable until a
-- human reconciles it. A row exists only while the room has unapplied grants: a completed
-- settlement deletes it, so the table stays proportional to in-flight or failed work, never
-- to the history behind it.

CREATE TABLE control.settle_task (
    room_id text PRIMARY KEY,
    tenant_id text NOT NULL,
    game_id text NOT NULL,
    status text NOT NULL,
    owner_instance_id text,
    lease_until timestamptz,
    attempt_count integer NOT NULL DEFAULT 0 CHECK (attempt_count >= 0),
    last_error text,
    next_attempt timestamptz,
    updated_at timestamptz NOT NULL DEFAULT clock_timestamp()
);

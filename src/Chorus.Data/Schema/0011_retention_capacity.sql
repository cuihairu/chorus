-- Retention bookkeeping and the capacity guard (architecture 9 data retention, AT21).
-- pruned_before_tick records how far a settled round's frame prefix was released; the
-- counter makes capacity rejections visible instead of silent.

CREATE SCHEMA IF NOT EXISTS ops;

ALTER TABLE game.room ADD COLUMN pruned_before_tick bigint NOT NULL DEFAULT 0;

CREATE TABLE ops.capacity_rejection (
    kind text PRIMARY KEY,
    count bigint NOT NULL DEFAULT 0,
    last_rejected_at timestamptz NOT NULL DEFAULT clock_timestamp()
);

-- End-operation block evidence (FIX-SETTLE-05): the release step's data-plane transaction
-- refuses while the end ladder's evidence is still gathering — undisposed inputs, an
-- incomplete reward manifest, a platform precondition. The reason is recorded here as the
-- queryable block, with the moment the continuous block began and a cumulative dwell across
-- all block episodes; the dwell is added to the total exactly when the release commits, so
-- a restarted control plane reads the block state and the room's snapshot exposes it.

ALTER TABLE control.end_operation
    ADD COLUMN blocked_reason text,
    ADD COLUMN blocked_since timestamptz,
    ADD COLUMN blocked_total_ms bigint NOT NULL DEFAULT 0;

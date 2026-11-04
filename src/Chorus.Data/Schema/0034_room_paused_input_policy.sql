-- FIX-SYNC-07: the paused-input policy a paused room answers under (authoritative-sync.md:52).
-- The declared policy rides the canonical offline_policy's fifth field, but the input store
-- branches on it per accepted event, so it is derived into its own typed column: countable
-- ("计数可核对"), queryable for the overflow surface, and derivable once at prepare without a
-- hot-path string parse or a GameServer-side reference. Existing rooms backfill from their
-- persisted declaration; a room with no declared policy stays '' and the gate fails closed.
ALTER TABLE game.room ADD COLUMN paused_input_policy text NOT NULL DEFAULT '';
UPDATE game.room SET paused_input_policy = split_part(offline_policy, '|', 5)
WHERE paused_input_policy = '' AND offline_policy <> '';

-- FIX-SYNC-04: persist the room's all-offline policy in its canonical form. Admission
-- validated the module's declaration (authoritative-sync.md:44) and the game host wrote
-- that exact string at prepare; a recovery compares the stored declaration against the
-- package's current one and refuses a mismatch, so a config upgrade can never silently
-- rewrite a live round's offline behavior. Empty = the room predates the declaration —
-- such a room is refused at recovery rather than silently assigned one.
ALTER TABLE game.room ADD COLUMN offline_policy text NOT NULL DEFAULT '';

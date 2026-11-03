-- FIX-SYNC-06: the persistent pause's durable facts. A room paused at a committed boundary
-- flips game.room.status to 'Paused' and records when the freeze began and why. The frozen
-- tick is the row's existing last_tick (the head the pause parked at); paused_at is the
-- wall-clock start of the pause — leases and other wall-clock maintenance keep running
-- while the logical tick and game clock stay frozen (authoritative-sync.md:42). NULL
-- paused_at = the room never paused; it stays NULL across every non-paused lifecycle.
ALTER TABLE game.room ADD COLUMN paused_at timestamptz;
ALTER TABLE game.room ADD COLUMN paused_reason text NOT NULL DEFAULT '';

-- FIX-MODULE-01: persist the room's recovery/checkpoint mode. Recovery previously rebuilt
-- the module from a hardcoded literal (RoomRuntime), so a takeover or admin retry could
-- silently change the checkpoint policy. The mode is a module fact written when the host
-- prepares the tick-0 snapshot and read back at recovery, so the room's own identity —
-- package, module version, config pin, tick cadence and recovery mode — is authoritative
-- instead of whatever the recovery caller supplies.
ALTER TABLE game.room ADD COLUMN recovery_mode text NOT NULL DEFAULT 'DeterministicReplay';

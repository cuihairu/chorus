-- FIX-TAKE-07: persist the room's tick cadence. The stall governor recovers a frozen room
-- on another host and must resume it at the cadence it was prepared with, not a guess.
ALTER TABLE game.room ADD COLUMN tick_ms integer NOT NULL DEFAULT 50;

-- M2-1: round generation and durable round attribution evidence (architecture 2, 6.2, 6.3).
-- live.binding.round_generation counts the rounds a mount has entered; input.event keeps the
-- first round attribution so dedup replays return the original room even after routing.

ALTER TABLE live.binding ADD COLUMN round_generation bigint NOT NULL DEFAULT 0;

ALTER TABLE input.event ADD COLUMN attributed_room_id text REFERENCES game.room(room_id);

-- True mapping conflicts wait for review; they must be scannable, never silently isolated.
CREATE INDEX ix_input_event_round_review ON input.event(received_at)
    WHERE disposition = 'AmbiguousRound';

-- The per-round inbox read path scans pending deliveries in sequence order.
CREATE INDEX ix_input_delivery_pending ON input.delivery(consumer_scope, sequence)
    WHERE state = 'Pending';

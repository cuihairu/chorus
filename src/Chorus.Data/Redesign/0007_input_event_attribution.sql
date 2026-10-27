-- Durable platform event intake (architecture 6.1): the receive transaction attributes each
-- event to the active binding on the canonical room key, or parks durable unattributed events
-- in the persistent inbox (PendingAttribution) for later reconciliation.
ALTER TABLE input.event ADD COLUMN canonical_room_key text;
ALTER TABLE input.event ADD COLUMN durability text NOT NULL DEFAULT 'Durable';

CREATE INDEX ix_input_event_pending ON input.event(platform, canonical_room_key)
    WHERE disposition = 'PendingAttribution';

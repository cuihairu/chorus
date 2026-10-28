-- M4 coordinator candidates (architecture 7.1). A queued ticket names the streamer behind
-- the mount, so the coordinator's candidate window is self-describing: pairing never needs
-- a second lookup, and the participant set of a reservation is exactly the queued tickets.

ALTER TABLE match.ticket ADD COLUMN streamer_id text NOT NULL;

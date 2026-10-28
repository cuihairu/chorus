-- M3 match participants and control-plane audit (architecture 7.1, 7.3, operations 6).
-- A reservation names the tickets it holds so prepare/commit/abort can move the whole
-- participant set atomically; queued tickets may carry their own expiry; every admin
-- command lands in an append-only audit row.

ALTER TABLE match.ticket ADD COLUMN reservation_id text REFERENCES match.reservation(reservation_id);
CREATE INDEX ix_match_ticket_reservation ON match.ticket(reservation_id);
ALTER TABLE match.ticket ADD COLUMN expires_at timestamptz;

CREATE TABLE control.audit (
    audit_id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    request_id text NOT NULL,
    operator text NOT NULL,
    reason text NOT NULL,
    scope text NOT NULL,
    command text NOT NULL,
    detail jsonb NOT NULL DEFAULT '{}'::jsonb,
    created_at timestamptz NOT NULL DEFAULT clock_timestamp()
);

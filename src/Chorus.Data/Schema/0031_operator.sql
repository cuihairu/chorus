-- GM operator accounts and the request fingerprint on the audit trail (FIX-GM-06,
-- architecture 12.1). An operator is a human GM identity, authenticated by a bearer token
-- that is separate from the process mTLS certificate: the certificate proves a process, never
-- an operator. The role ranks the commands an operator may run; tenants/games scope the target
-- an operator may act on (empty array = all). The token is stored as a SHA-256 digest so the
-- store never holds the bearer secret, and the operator row is the only place a human GM
-- identity exists — the Admin request's `operator` field is never trusted as an identity.

CREATE TABLE control.operator (
    operator_id text PRIMARY KEY,
    role text NOT NULL,
    tenants text[] NOT NULL DEFAULT '{}',
    games text[] NOT NULL DEFAULT '{}',
    token_hash bytea NOT NULL,
    enabled boolean NOT NULL DEFAULT true,
    created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    updated_at timestamptz NOT NULL DEFAULT clock_timestamp()
);

CREATE INDEX ix_control_operator_token ON control.operator(token_hash);

-- The request fingerprint (SHA-256 of the canonical detail) joins an audit row to the exact
-- request shape: the same OperationId with a different fingerprint is a conflict, and a lost
-- reply can be matched to the recorded outcome (FIX-GM-06). Nullable so pre-existing rows
-- keep their shape; every new audited command records it.
ALTER TABLE control.audit ADD COLUMN fingerprint bytea;

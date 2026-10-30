CREATE SCHEMA IF NOT EXISTS infra;

CREATE TABLE IF NOT EXISTS infra.operation_receipt (
    tenant_id text NOT NULL,
    game_id text NOT NULL,
    operation_kind text NOT NULL,
    target_id text NOT NULL,
    operation_id text NOT NULL,
    canonical_version integer NOT NULL CHECK (canonical_version > 0),
    request_hash char(64) NOT NULL CHECK (request_hash ~ '^[0-9A-F]{64}$'),
    result_json jsonb NOT NULL,
    committed_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    PRIMARY KEY (tenant_id, game_id, operation_kind, target_id, operation_id)
);

-- Settlement manifest amounts and the leaderboard projection (architecture 9.2, 9.3).
-- The final frame writes the immutable reward manifest: every grant row carries its own
-- asset and amount, so the settler applies committed values instead of re-deriving them
-- from a result document.

ALTER TABLE settlement.grant ADD COLUMN asset_id text NOT NULL DEFAULT 'points';
ALTER TABLE settlement.grant ADD COLUMN delta bigint NOT NULL DEFAULT 0;

-- Projection of committed ledger lines (architecture 9.2): leaderboards read this table,
-- and it is only ever written inside the same transaction as the ledger line itself, so a
-- visible score is always backed by a committed grant. The rank index orders deterministically
-- (score descending, player id ascending).
CREATE TABLE IF NOT EXISTS game.leaderboard_score (
    tenant_id text NOT NULL,
    game_id text NOT NULL,
    board_id text NOT NULL,
    player_id text NOT NULL,
    score bigint NOT NULL,
    updated_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    PRIMARY KEY (tenant_id, game_id, board_id, player_id)
);
CREATE INDEX ix_game_leaderboard_rank ON game.leaderboard_score(tenant_id, game_id, board_id, score DESC, player_id);

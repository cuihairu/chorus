-- FIX-RANK-02: the default persistent leaderboard projection (developer-capabilities.md
-- 「排行榜定义」「标识」). One row per subject entry of one board on one period in one scope:
-- the board's identity is the five-part key (tenant, game, board, period, scope) plus the
-- declared subject type and the subject id — a streamer entry and a viewer entry that share a
-- string id are different subjects (RANK-01), so both are part of the key. The score is the
-- board's projection value under its declared aggregation and bounds; the bounds are a
-- per-board declaration (a table CHECK cannot express them — one board may allow negatives
-- while another does not), so the contract layer (LeaderboardScoring.TryAccumulate) enforces
-- them and a refused delta never writes a row here.
--
-- The rank index covers exactly the board scope and the declared read order: equality on the
-- five-part scope plus subject type, then score descending and subject id ascending. A TopN
-- read of one board is an index-only range walk, never a scan of another board's or another
-- period's rows — a board's entries never bleed across periods, scopes or tenants.
CREATE TABLE IF NOT EXISTS game.leaderboard_entry (
    tenant_id    text        NOT NULL,
    game_id      text        NOT NULL,
    board_id     text        NOT NULL,
    period_id    text        NOT NULL,
    scope_id     text        NOT NULL,
    subject_type smallint    NOT NULL,
    subject_id   text        NOT NULL,
    score        bigint      NOT NULL,
    updated_at   timestamptz NOT NULL DEFAULT clock_timestamp(),
    PRIMARY KEY (tenant_id, game_id, board_id, period_id, scope_id, subject_type, subject_id)
);
CREATE INDEX IF NOT EXISTS ix_game_leaderboard_entry_rank
    ON game.leaderboard_entry(tenant_id, game_id, board_id, period_id, scope_id,
        subject_type, score DESC, subject_id);

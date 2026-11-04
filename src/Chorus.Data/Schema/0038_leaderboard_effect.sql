-- FIX-RANK-03: the idempotent score projection ledger (developer-capabilities.md
-- 「Score changes derive from immutable committed server effects, identified by
-- EffectId/GrantId」). One row per applied scoring effect, keyed by its immutable
-- EffectId — a concurrent, replayed or lost-response retry of the same effect is a
-- duplicate no-op, never a second delta; the same EffectId re-projected to a different
-- board, subject or delta is a visible conflict, never a silent move. The row records
-- which board the effect projected onto (the business module's declared resolution) and
-- the resulting score, so a projection rebuild (FIX-RANK-08) can replay the effect
-- ledger in order without re-reading the frames.
CREATE TABLE IF NOT EXISTS game.leaderboard_effect (
    effect_id    text        NOT NULL,
    tenant_id    text        NOT NULL,
    game_id      text        NOT NULL,
    board_id     text        NOT NULL,
    period_id    text        NOT NULL,
    scope_id     text        NOT NULL,
    subject_type smallint    NOT NULL,
    subject_id   text        NOT NULL,
    delta        bigint      NOT NULL,
    score        bigint      NOT NULL,
    applied_at   timestamptz NOT NULL DEFAULT clock_timestamp(),
    PRIMARY KEY (effect_id)
);

-- A board's effect ledger (diagnostics, per-board audit and the rebuild walk): the
-- effects that hit one board answer in one index range, never a scan of every board.
CREATE INDEX IF NOT EXISTS ix_game_leaderboard_effect_board
    ON game.leaderboard_effect(tenant_id, game_id, board_id, period_id, scope_id);

-- FIX-RANK-08: GM correction and projection rebuild (developer-capabilities.md
-- 「GM adjustment/reset names the board, period and scope and includes expected version,
-- operation ID, reason and audit evidence. Original scoring evidence remains available」).
-- The effect ledger is the rebuild source, so a GM correction must be a ledger row too —
-- otherwise a rebuild would silently erase every GM correction ever applied. The kind column
-- separates committed game score effects ('score', FIX-RANK-03) from GM maintenance rows
-- ('gm-adjust': a signed correction delta; 'gm-reset': zero one subject's entry, or the whole
-- board's when the row carries no subject). reason and operator_id are the audit evidence the
-- capability requires — the game effects keep empty defaults, their evidence is the committed
-- outbox row the effect id names. A rebuild replays the board's ledger rows in apply order,
-- so the projection (entries) is always re-derivable from the ledger, never the other way
-- around, and original scoring evidence is never overwritten.
ALTER TABLE game.leaderboard_effect
    ADD COLUMN kind text NOT NULL DEFAULT 'score',
    ADD COLUMN reason text NOT NULL DEFAULT '',
    ADD COLUMN operator_id text NOT NULL DEFAULT '';

-- The canonical live session key links a live broadcast to its cross-GameId mount slot.
-- Nullable keeps the migration safe for rows written before the link existed.
ALTER TABLE live.session ADD COLUMN canonical_live_session_key text;
CREATE UNIQUE INDEX ux_live_session_key
    ON live.session(platform, canonical_live_session_key)
    WHERE canonical_live_session_key IS NOT NULL;

-- FIX-SYNC-09: the client-command frequency cap's supporting index (authoritative-sync.md
-- anti-cheat table, 重放 row). The intake counts a binding's own @chorus rows inside the
-- rolling window before inserting; this partial index keeps that count a range read over
-- the binding's recent commands instead of a scan of the whole event log. Platform events
-- never enter the count — the cap bounds the untrusted client channel only.
CREATE INDEX IF NOT EXISTS ix_input_event_client_rate
    ON input.event(binding_id, received_at)
    WHERE platform = '@chorus';

-- FIX-RANK-07: the immutable source time of a committed effect (developer-capabilities.md
-- 「周期」「迟到结算有明确的周期归属」). A committed score effect must be attributed to the
-- period of its source time — never to the period of its projection/retry time, which would
-- silently move a late effect to the wrong bucket. The committed effect ledger carries no
-- timestamp today (the payload is deterministic by design, so the module cannot stamp a
-- wall-clock into it), so the ledger itself records when the effect was committed. The
-- timestamp is captured once at INSERT (DEFAULT clock_timestamp()) and never refreshed by a
-- retry, so a replayed or delayed delivery answers the same source time every time.
ALTER TABLE infra.outbox
    ADD COLUMN committed_at timestamptz NOT NULL DEFAULT clock_timestamp();

-- M6 deployment plans (architecture 10.2). A plan carries the command identity and its
-- expiry so an agent can deduplicate re-delivered plans and drop stale ones: a repeat of
-- the same CommandId must never pull a second process, and an expired plan is refused.

ALTER TABLE control.deployment ADD COLUMN command_id text;
ALTER TABLE control.deployment ADD COLUMN command_expires_at timestamptz;

-- External task coordination (architecture 6.3): attempt bookkeeping, last error, the executor
-- lease and the in-flight attempt owner/deadline. The executing partial unique index enforces
-- serial external operations per mount; a reassigned epoch never clears an in-flight attempt.
ALTER TABLE live.task ADD COLUMN attempt_count integer NOT NULL DEFAULT 0;
ALTER TABLE live.task ADD COLUMN last_error text;
ALTER TABLE live.task ADD COLUMN lease_owner text;
ALTER TABLE live.task ADD COLUMN lease_until timestamptz;
ALTER TABLE live.task ADD COLUMN attempt_owner text;
ALTER TABLE live.task ADD COLUMN attempt_deadline timestamptz;

CREATE UNIQUE INDEX ux_live_task_executing ON live.task(binding_id) WHERE status = 'Executing';

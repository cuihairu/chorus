-- Session connection lifecycle: gateway switches increment the connection
-- generation; a matching close marks the session offline. Stale closes carry
-- an old generation and must not touch the new connection.
ALTER TABLE auth.session ADD COLUMN connection_closed_at timestamptz;

-- AttachConnection rotation rows remember the generation they produced so
-- exact retries inside the retry window return the same result.
ALTER TABLE auth.rotation ADD COLUMN result_connection_generation bigint;

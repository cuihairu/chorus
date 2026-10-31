-- FIX-END-03: the routing version each PlatformGateway installed and confirmed. The gateway
-- answers resolves from one snapshot version; until it confirms that version back to the
-- control plane, its face refuses to push — an unconfirmed picture guesses no rooms. Keyed by
-- the gateway's process identity, taken from its certificate, never from the request.
CREATE TABLE control.routing_confirm (
    process_instance_id text PRIMARY KEY,
    version bigint NOT NULL,
    confirmed_at timestamptz NOT NULL DEFAULT clock_timestamp()
);

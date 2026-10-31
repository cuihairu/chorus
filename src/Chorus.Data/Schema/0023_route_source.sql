-- FIX-END-02: the mount's route source, registered by the StreamerServer after the mount
-- commits. One row per streamer and game holds the binding the mounts entered at which
-- generation, with the platform identity the login verified — derived by the control plane
-- from the live session join, never asserted by the reporter. A late report naming an older
-- generation never overwrites the newer row.
CREATE TABLE control.route_source (
    tenant_id text NOT NULL,
    game_id text NOT NULL,
    streamer_id text NOT NULL,
    binding_id text NOT NULL,
    platform text NOT NULL,
    canonical_live_session_key text NOT NULL,
    binding_generation bigint NOT NULL,
    room_id text NOT NULL DEFAULT '',
    registered_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    updated_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    PRIMARY KEY (tenant_id, game_id, streamer_id)
);

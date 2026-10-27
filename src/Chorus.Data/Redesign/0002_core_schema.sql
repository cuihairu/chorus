CREATE SCHEMA IF NOT EXISTS control;
CREATE SCHEMA IF NOT EXISTS auth;
CREATE SCHEMA IF NOT EXISTS live;
CREATE SCHEMA IF NOT EXISTS player;
CREATE SCHEMA IF NOT EXISTS match;
CREATE SCHEMA IF NOT EXISTS game;
CREATE SCHEMA IF NOT EXISTS input;
CREATE SCHEMA IF NOT EXISTS settlement;

CREATE TABLE control.node (
    node_id text PRIMARY KEY,
    agent_instance_id text NOT NULL,
    report_sequence bigint NOT NULL DEFAULT 0 CHECK (report_sequence >= 0),
    observed_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    capabilities jsonb NOT NULL DEFAULT '{}'::jsonb,
    lifecycle text NOT NULL DEFAULT 'Starting',
    readiness text NOT NULL DEFAULT 'NotReady',
    health text NOT NULL DEFAULT 'Healthy',
    admission text NOT NULL DEFAULT 'Accepting'
);

CREATE TABLE control.deployment (
    slot_id text PRIMARY KEY,
    node_id text NOT NULL REFERENCES control.node(node_id),
    role text NOT NULL,
    desired_version text NOT NULL,
    plan_generation bigint NOT NULL CHECK (plan_generation > 0),
    desired_state text NOT NULL,
    package_hash text NOT NULL,
    updated_at timestamptz NOT NULL DEFAULT clock_timestamp()
);

CREATE TABLE control.process (
    process_instance_id text PRIMARY KEY,
    slot_id text NOT NULL REFERENCES control.deployment(slot_id),
    node_id text NOT NULL REFERENCES control.node(node_id),
    agent_instance_id text NOT NULL,
    report_sequence bigint NOT NULL CHECK (report_sequence >= 0),
    lifecycle text NOT NULL,
    readiness text NOT NULL,
    health text NOT NULL,
    admission text NOT NULL,
    entity_recovery text NOT NULL DEFAULT 'NotStarted',
    started_at timestamptz NOT NULL,
    observed_at timestamptz NOT NULL DEFAULT clock_timestamp()
);
CREATE INDEX ix_control_process_node ON control.process(node_id, slot_id);

CREATE TABLE control.coordinator (
    partition_key text PRIMARY KEY,
    holder_instance_id text NOT NULL,
    term bigint NOT NULL CHECK (term > 0),
    lease_until timestamptz NOT NULL
);

CREATE TABLE control.ownership (
    kind text NOT NULL,
    entity_id text NOT NULL,
    owner_process_instance_id text NOT NULL,
    epoch bigint NOT NULL CHECK (epoch > 0),
    lease_until timestamptz NOT NULL,
    status text NOT NULL,
    version bigint NOT NULL CHECK (version > 0),
    PRIMARY KEY (kind, entity_id)
);
CREATE INDEX ix_control_ownership_owner ON control.ownership(owner_process_instance_id);

CREATE TABLE auth.identity (
    platform text NOT NULL,
    app_id text NOT NULL,
    open_id text NOT NULL,
    tenant_id text NOT NULL,
    player_id text NOT NULL,
    created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    PRIMARY KEY (platform, app_id, open_id),
    UNIQUE (tenant_id, player_id, platform, app_id)
);

CREATE TABLE auth.streamer_state (
    streamer_id text PRIMARY KEY,
    tenant_id text NOT NULL,
    current_session_version bigint NOT NULL DEFAULT 0 CHECK (current_session_version >= 0),
    current_session_id text,
    eligibility_version bigint NOT NULL DEFAULT 0,
    ban_state text NOT NULL DEFAULT 'Allowed'
);

CREATE TABLE auth.session (
    session_id text PRIMARY KEY,
    streamer_id text NOT NULL REFERENCES auth.streamer_state(streamer_id),
    session_version bigint NOT NULL CHECK (session_version > 0),
    gateway_instance_id text NOT NULL,
    connection_id text NOT NULL,
    connection_generation bigint NOT NULL DEFAULT 1,
    resume_hash bytea NOT NULL,
    expires_at timestamptz NOT NULL,
    revoked_at timestamptz,
    UNIQUE (streamer_id, session_version)
);

CREATE TABLE auth.rotation (
    streamer_id text NOT NULL REFERENCES auth.streamer_state(streamer_id),
    operation_id text NOT NULL,
    previous_secret_hash bytea NOT NULL,
    request_hash bytea NOT NULL,
    result_session_id text NOT NULL,
    result_session_version bigint NOT NULL,
    retry_until timestamptz NOT NULL,
    PRIMARY KEY (streamer_id, operation_id)
);

CREATE TABLE live.session (
    live_session_id text PRIMARY KEY,
    tenant_id text NOT NULL,
    streamer_id text NOT NULL REFERENCES auth.streamer_state(streamer_id),
    platform text NOT NULL,
    app_id text NOT NULL,
    platform_room_key text NOT NULL,
    started_at timestamptz NOT NULL,
    ended_at timestamptz
);

CREATE TABLE live.slot (
    platform text NOT NULL,
    canonical_live_session_key text NOT NULL,
    tenant_id text NOT NULL,
    active_binding_id text,
    generation bigint NOT NULL DEFAULT 0 CHECK (generation >= 0),
    PRIMARY KEY (platform, canonical_live_session_key)
);

CREATE TABLE live.binding (
    binding_id text PRIMARY KEY,
    live_session_id text NOT NULL REFERENCES live.session(live_session_id),
    tenant_id text NOT NULL,
    game_id text NOT NULL,
    generation bigint NOT NULL CHECK (generation > 0),
    state text NOT NULL,
    current_room_id text,
    created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    UNIQUE (live_session_id, generation)
);
CREATE UNIQUE INDEX ux_live_binding_active ON live.binding(live_session_id) WHERE state IN ('Starting', 'Ready', 'Running', 'AwaitingPlatformClose');

CREATE TABLE live.task (
    task_id text PRIMARY KEY,
    binding_id text NOT NULL REFERENCES live.binding(binding_id),
    binding_generation bigint NOT NULL,
    task_epoch bigint NOT NULL CHECK (task_epoch > 0),
    desired jsonb NOT NULL,
    observed jsonb,
    operation_id text NOT NULL,
    status text NOT NULL,
    next_attempt timestamptz,
    UNIQUE (binding_id, operation_id)
);

CREATE TABLE player.profile (
    tenant_id text NOT NULL,
    game_id text NOT NULL,
    player_id text NOT NULL,
    version bigint NOT NULL DEFAULT 1 CHECK (version > 0),
    schema_version integer NOT NULL CHECK (schema_version > 0),
    data jsonb NOT NULL DEFAULT '{}'::jsonb,
    PRIMARY KEY (tenant_id, game_id, player_id)
);

CREATE TABLE player.asset (
    tenant_id text NOT NULL,
    game_id text NOT NULL,
    player_id text NOT NULL,
    asset_id text NOT NULL,
    balance bigint NOT NULL DEFAULT 0 CHECK (balance >= 0),
    version bigint NOT NULL DEFAULT 1 CHECK (version > 0),
    PRIMARY KEY (tenant_id, game_id, player_id, asset_id)
);

CREATE TABLE player.ledger (
    tenant_id text NOT NULL,
    game_id text NOT NULL,
    operation_id text NOT NULL,
    line_id text NOT NULL,
    player_id text NOT NULL,
    asset_id text NOT NULL,
    delta bigint NOT NULL,
    reason text NOT NULL,
    resulting_balance bigint NOT NULL CHECK (resulting_balance >= 0),
    committed_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    PRIMARY KEY (tenant_id, game_id, operation_id, line_id)
);
CREATE INDEX ix_player_ledger_account ON player.ledger(tenant_id, game_id, player_id, asset_id, committed_at);

CREATE TABLE match.ticket (
    ticket_id text PRIMARY KEY,
    tenant_id text NOT NULL,
    game_id text NOT NULL,
    binding_id text NOT NULL REFERENCES live.binding(binding_id),
    state text NOT NULL,
    version bigint NOT NULL CHECK (version > 0),
    partition_key text NOT NULL,
    created_at timestamptz NOT NULL DEFAULT clock_timestamp()
);
CREATE UNIQUE INDEX ux_match_ticket_active ON match.ticket(binding_id) WHERE state IN ('Queued', 'Reserved', 'Preparing');

CREATE TABLE match.reservation (
    reservation_id text PRIMARY KEY,
    room_id text NOT NULL UNIQUE,
    partition_key text NOT NULL,
    coordinator_term bigint NOT NULL CHECK (coordinator_term > 0),
    expires_at timestamptz NOT NULL,
    prepared_receipt jsonb,
    status text NOT NULL
);

CREATE TABLE game.room (
    room_id text PRIMARY KEY,
    tenant_id text NOT NULL,
    game_id text NOT NULL,
    status text NOT NULL,
    epoch bigint NOT NULL CHECK (epoch > 0),
    last_tick bigint NOT NULL DEFAULT 0 CHECK (last_tick >= 0),
    state_version bigint NOT NULL DEFAULT 0 CHECK (state_version >= 0),
    state_hash bytea,
    package_hash text NOT NULL,
    module_version text NOT NULL,
    config_hash text NOT NULL,
    updated_at timestamptz NOT NULL DEFAULT clock_timestamp()
);

CREATE TABLE game.participation (
    room_id text NOT NULL REFERENCES game.room(room_id),
    participant_id text NOT NULL,
    binding_id text REFERENCES live.binding(binding_id),
    role text NOT NULL,
    state text NOT NULL,
    PRIMARY KEY (room_id, participant_id)
);
CREATE UNIQUE INDEX ux_game_participation_active ON game.participation(binding_id) WHERE binding_id IS NOT NULL AND state IN ('Active', 'Offline');

CREATE TABLE input.event (
    event_id text PRIMARY KEY,
    platform text NOT NULL,
    app_id text NOT NULL,
    source_dedup_key text NOT NULL,
    payload_hash bytea NOT NULL,
    received_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    disposition text NOT NULL,
    binding_id text REFERENCES live.binding(binding_id),
    binding_generation bigint,
    payload jsonb NOT NULL,
    UNIQUE (platform, app_id, source_dedup_key)
);

CREATE TABLE input.cursor (
    consumer_scope text PRIMARY KEY,
    next_delivery_sequence bigint NOT NULL DEFAULT 1 CHECK (next_delivery_sequence > 0),
    consumed_contiguous_sequence bigint NOT NULL DEFAULT 0 CHECK (consumed_contiguous_sequence >= 0)
);

CREATE TABLE input.delivery (
    event_id text NOT NULL REFERENCES input.event(event_id),
    consumer_scope text NOT NULL REFERENCES input.cursor(consumer_scope),
    room_id text REFERENCES game.room(room_id),
    route_version bigint NOT NULL CHECK (route_version > 0),
    sequence bigint NOT NULL CHECK (sequence > 0),
    state text NOT NULL,
    PRIMARY KEY (event_id, consumer_scope),
    UNIQUE (consumer_scope, sequence)
);

CREATE TABLE game.frame (
    room_id text NOT NULL REFERENCES game.room(room_id),
    tick_id bigint NOT NULL CHECK (tick_id > 0),
    commit_id text NOT NULL UNIQUE,
    input_refs jsonb NOT NULL,
    state_hash bytea NOT NULL,
    effects jsonb NOT NULL,
    module_version text NOT NULL,
    config_hash text NOT NULL,
    PRIMARY KEY (room_id, tick_id)
);

CREATE TABLE game.snapshot (
    room_id text NOT NULL REFERENCES game.room(room_id),
    tick_id bigint NOT NULL CHECK (tick_id >= 0),
    schema_version integer NOT NULL CHECK (schema_version > 0),
    payload bytea NOT NULL,
    payload_hash bytea NOT NULL,
    verified boolean NOT NULL DEFAULT false,
    PRIMARY KEY (room_id, tick_id, schema_version)
);

CREATE TABLE game.result (
    room_id text NOT NULL REFERENCES game.room(room_id),
    result_version bigint NOT NULL CHECK (result_version > 0),
    final_frame bigint NOT NULL,
    plan_hash bytea NOT NULL,
    result jsonb NOT NULL,
    PRIMARY KEY (room_id, result_version)
);

CREATE TABLE settlement.grant (
    room_id text NOT NULL,
    result_version bigint NOT NULL,
    player_id text NOT NULL,
    grant_key text NOT NULL,
    status text NOT NULL,
    operation_id text NOT NULL,
    PRIMARY KEY (room_id, result_version, player_id, grant_key),
    FOREIGN KEY (room_id, result_version) REFERENCES game.result(room_id, result_version)
);

CREATE TABLE infra.outbox (
    effect_id text PRIMARY KEY,
    kind text NOT NULL,
    payload jsonb NOT NULL,
    status text NOT NULL,
    next_attempt timestamptz,
    attempt_count integer NOT NULL DEFAULT 0 CHECK (attempt_count >= 0)
);

CREATE TABLE infra.audit (
    audit_id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    actor_instance_id text NOT NULL,
    operation_id text NOT NULL,
    action text NOT NULL,
    target text NOT NULL,
    reason text NOT NULL,
    before_hash bytea,
    after_hash bytea,
    at timestamptz NOT NULL DEFAULT clock_timestamp()
);

ALTER TABLE control.ownership ADD COLUMN tenant_id text NOT NULL DEFAULT '@cluster';
ALTER TABLE control.ownership ADD COLUMN game_id text NOT NULL DEFAULT '@cluster';
CREATE INDEX ix_control_ownership_scope ON control.ownership(tenant_id, game_id, kind, entity_id);

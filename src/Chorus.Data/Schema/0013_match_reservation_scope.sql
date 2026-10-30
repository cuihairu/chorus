-- M3 reservation scope and participant set (architecture 7.2). A reservation names the
-- tenant/game it belongs to and freezes the participant list it reserved, so prepare and
-- commit always act on exactly the set that was locked — never a re-derived one.

ALTER TABLE match.reservation ADD COLUMN tenant_id text NOT NULL;
ALTER TABLE match.reservation ADD COLUMN game_id text NOT NULL;
ALTER TABLE match.reservation ADD COLUMN participants jsonb NOT NULL DEFAULT '[]'::jsonb;

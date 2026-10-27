ALTER TABLE player.ledger DROP CONSTRAINT ledger_pkey;
ALTER TABLE player.ledger ADD PRIMARY KEY (tenant_id, game_id, player_id, operation_id, line_id);

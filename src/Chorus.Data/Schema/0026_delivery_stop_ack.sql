-- FIX-END-07: the step-4 confirmation's persisted facts (architecture 6.4). The first ack
-- of a responder freezes its evidence: the cutoff sequence — the highest delivery sequence
-- this gateway observed for the room through its push leg, a subset of the room's shared
-- cursor — and the informational drain counts. Defaults keep rows written by FIX-END-05
-- valid; answered_at flips from null when the state leaves Pending. First answer wins: a
-- repeated ack at the recorded version changes nothing, so a restarted gateway's re-ack
-- (honest zeros after a crash) never lowers the recorded watermark.

ALTER TABLE control.stop_delivery_responder
    ADD COLUMN cutoff_sequence bigint NOT NULL DEFAULT 0,
    ADD COLUMN drained_out_of_round bigint NOT NULL DEFAULT 0,
    ADD COLUMN drained_routed bigint NOT NULL DEFAULT 0;

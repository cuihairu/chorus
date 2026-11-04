-- FIX-SYNC-08: one durable ready row per control participant of a paused room
-- (authoritative-sync.md:53-54). A ready is a declaration that a current-generation control
-- session applied the frozen head as its baseline. The row is bound to the room's epoch and
-- the frozen boundary it prepared against, and carries the binding/connection generations
-- the session reported — so a stale (old-generation) session, a changed epoch, or a boundary
-- that is no longer the head never writes a counted ready. PK (room_id, participant_id)
-- makes re-reporting idempotent per participant. Rows live only while the room is Paused:
-- ResumeRound and the pause-timeout result each delete the room's rows in the same
-- transaction, so the table never accumulates a finished room's readiness.
CREATE TABLE IF NOT EXISTS game.resume_ready (
    room_id              text        NOT NULL REFERENCES game.room(room_id),
    participant_id       text        NOT NULL,
    binding_id           text        NOT NULL,
    binding_generation   bigint      NOT NULL,
    connection_generation bigint     NOT NULL,
    epoch                bigint      NOT NULL,
    baseline_tick        bigint      NOT NULL,
    baseline_version     bigint      NOT NULL,
    ready_at             timestamptz NOT NULL DEFAULT clock_timestamp(),
    PRIMARY KEY (room_id, participant_id)
);
CREATE INDEX IF NOT EXISTS ux_resume_ready_room ON game.resume_ready(room_id);

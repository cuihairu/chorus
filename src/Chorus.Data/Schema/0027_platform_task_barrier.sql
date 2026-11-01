-- FIX-END-09: the platform end task's barrier binding (architecture 6.4). A task that
-- carries an end operation id here is gated: no executor may issue its external call until
-- the standing end operation's phase reaches PlatformTaskAuthorized — the phase row IS the
-- persistent credential, checked live at execution time so a restart, a reassignment or a
-- racing enqueue cannot slide past the delivery-stop barrier. NULL (the default) keeps every
-- FIX-END-05-era task ungated: M1's launch-time semantics are unchanged.

ALTER TABLE live.task ADD COLUMN barrier_end_operation_id text;

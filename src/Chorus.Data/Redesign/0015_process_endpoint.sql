-- M5 instance registration (architecture 4.4). A process report carries the endpoint the
-- process serves on, so the routing snapshot joins owners and streamers to reachable
-- endpoints without a second lookup. Nullable: bootstrap-era reports may omit it.

ALTER TABLE control.process ADD COLUMN endpoint text;

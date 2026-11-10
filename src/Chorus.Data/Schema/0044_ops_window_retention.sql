-- FIX-OBS-13: the ops sample tables keep their history (append-only, OBS-06/07/10/11/12
-- all report through them), and both the windowed aggregate reads and the retention
-- pruning bound their work by time — give each parent sample table a time index so a
-- window scan and a retention DELETE touch only the affected range instead of walking
-- the whole series. The child tables (disks, processes) carry no clock of their own:
-- they ride their machine_sample parent and fall with it (CASCADE).
CREATE INDEX app_counter_sample_time_idx
    ON ops.app_counter_sample(reported_at);
CREATE INDEX machine_sample_time_idx
    ON ops.machine_sample(sampled_at);

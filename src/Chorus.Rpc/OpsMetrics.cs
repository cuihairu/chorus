namespace Chorus.Rpc;

/// <summary>The kind of an ops metric: a gauge reads a level, a counter counts
/// monotone events, a histogram samples a distribution.</summary>
public enum OpsMetricKind
{
    Gauge,
    Counter,
    Histogram,
}

/// <summary>One canonical entry of the ops metric dictionary (FIX-OBS-01).</summary>
public sealed record OpsMetricSpec(
    string Name,
    OpsMetricKind Kind,
    string Unit,
    string Labels,
    string Description);

/// <summary>
/// The ops metric dictionary (FIX-OBS-01): the canonical registry of every
/// <c>chorus_*</c> metric the OBS families name — machine resources (OBS-02..05),
/// machine telemetry reporting (OBS-06), machine and process stats (OBS-07), room
/// stats (OBS-08), online by dimension (OBS-09), platform raw ingress (OBS-10), the
/// event delivery and effect chain (OBS-11) and outbound platform API calls
/// (OBS-12). Downstream tasks instrument against these names; a name a task needs
/// but the dictionary lacks is a dictionary amendment in that task, not a new string
/// literal. Prometheus naming: counters end <c>_total</c>, labels stay on bounded
/// dimensions — never per-player or per-room IDs (architecture §12 cardinality rule).
/// The names that already exist on the stage-3 text face are registered here so the
/// OBS tasks extend them instead of inventing duplicates.
/// </summary>
public static class OpsMetrics
{
    // Machine resources (OBS-02..05): a node reports its own box — no labels, the
    // series is the node. Memory percent is used/total derived, kept as its own entry
    // because an alert (OBS-15) reads a named percent, not a ratio.
    public static readonly OpsMetricSpec MachineCpuPercent =
        new("chorus_machine_cpu_percent", OpsMetricKind.Gauge, "percent", "",
            "The node's overall CPU usage, 0-100.");
    public static readonly OpsMetricSpec MachineMemoryUsedBytes =
        new("chorus_machine_memory_used_bytes", OpsMetricKind.Gauge, "bytes", "",
            "The node's used physical memory.");
    public static readonly OpsMetricSpec MachineMemoryTotalBytes =
        new("chorus_machine_memory_total_bytes", OpsMetricKind.Gauge, "bytes", "",
            "The node's total physical memory.");
    public static readonly OpsMetricSpec MachineMemoryPercent =
        new("chorus_machine_memory_percent", OpsMetricKind.Gauge, "percent", "",
            "The node's used physical memory as a percentage of total, 0-100.");
    public static readonly OpsMetricSpec MachineLoad1m =
        new("chorus_machine_load_1m", OpsMetricKind.Gauge, "", "",
            "The 1-minute system load average.");
    public static readonly OpsMetricSpec MachineLoad5m =
        new("chorus_machine_load_5m", OpsMetricKind.Gauge, "", "",
            "The 5-minute system load average.");
    public static readonly OpsMetricSpec MachineLoad15m =
        new("chorus_machine_load_15m", OpsMetricKind.Gauge, "", "",
            "The 15-minute system load average.");
    public static readonly OpsMetricSpec MachineDiskUsedBytes =
        new("chorus_machine_disk_used_bytes", OpsMetricKind.Gauge, "bytes", "mount",
            "Bytes used on a mounted filesystem (label: mount).");
    public static readonly OpsMetricSpec MachineDiskTotalBytes =
        new("chorus_machine_disk_total_bytes", OpsMetricKind.Gauge, "bytes", "mount",
            "Total bytes on a mounted filesystem (label: mount).");
    public static readonly OpsMetricSpec MachineDiskPercent =
        new("chorus_machine_disk_percent", OpsMetricKind.Gauge, "percent", "mount",
            "Used bytes as a percentage of the mounted filesystem, 0-100 (label: mount).");
    public static readonly OpsMetricSpec MachineUptimeSeconds =
        new("chorus_machine_uptime_seconds", OpsMetricKind.Gauge, "seconds", "",
            "Seconds since the node booted.");

    // Process stats (OBS-07): per process instance — a bounded set on a node, so the
    // process_instance_id label does not violate the cardinality rule. The stage-3
    // renderer already emits an unlabeled chorus_process_cpu_percent; the dictionary
    // form carries the process dimensions the machine/process stats API needs.
    public static readonly OpsMetricSpec ProcessCpuPercent =
        new("chorus_process_cpu_percent", OpsMetricKind.Gauge, "percent",
            "process_instance_id,role", "A supervised process's CPU usage, 0-100.");
    public static readonly OpsMetricSpec ProcessMemoryUsedBytes =
        new("chorus_process_memory_used_bytes", OpsMetricKind.Gauge, "bytes",
            "process_instance_id,role", "A supervised process's resident memory.");
    public static readonly OpsMetricSpec ProcessUptimeSeconds =
        new("chorus_process_uptime_seconds", OpsMetricKind.Gauge, "seconds",
            "process_instance_id", "Seconds since a supervised process started.");
    public static readonly OpsMetricSpec ProcessesRunning =
        new("chorus_processes_running", OpsMetricKind.Gauge, "count", "",
            "The number of supervised processes the node reports running.");

    // Room stats (OBS-08): room counts are levels, the state/tenant splits are the
    // bounded dimensions the room stats API groups by.
    public static readonly OpsMetricSpec Rooms =
        new("chorus_rooms", OpsMetricKind.Gauge, "count", "",
            "The number of rooms the cluster holds.");
    public static readonly OpsMetricSpec RoomsByState =
        new("chorus_rooms_by_state", OpsMetricKind.Gauge, "count", "state",
            "Rooms grouped by their ownership state (label: state).");
    public static readonly OpsMetricSpec RoomsByTenant =
        new("chorus_rooms_by_tenant", OpsMetricKind.Gauge, "count", "tenant",
            "Rooms grouped by tenant (label: tenant).");

    // Online by dimension (OBS-09): the 分口径 breakdown — tenant/game are the
    // bounded roster dimensions; per-streamer splits are an API query over the roster,
    // not a metric label (a streamer label would scale with the roster).
    public static readonly OpsMetricSpec OnlinePlayers =
        new("chorus_online_players", OpsMetricKind.Gauge, "count", "tenant,game",
            "Online players, broken down by tenant and game.");
    public static readonly OpsMetricSpec OnlineStreamers =
        new("chorus_online_streamers", OpsMetricKind.Gauge, "count", "tenant",
            "Online streamers, broken down by tenant.");
    public static readonly OpsMetricSpec OnlineRooms =
        new("chorus_online_rooms", OpsMetricKind.Gauge, "count", "tenant,game",
            "Rooms with at least one participant, broken down by tenant and game.");

    // Platform raw ingress (OBS-10): the gateway face names already emitted by the
    // stage-3 renderer are registered here, plus the raw input counter.
    public static readonly OpsMetricSpec IngressAcceptedTotal =
        new("chorus_ingress_accepted_total", OpsMetricKind.Counter, "count", "",
            "Ingress events accepted for delivery.");
    public static readonly OpsMetricSpec IngressDuplicatesTotal =
        new("chorus_ingress_duplicates_total", OpsMetricKind.Counter, "count", "",
            "Ingress events refused as duplicates.");
    public static readonly OpsMetricSpec IngressUnverifiedTotal =
        new("chorus_ingress_unverified_total", OpsMetricKind.Counter, "count", "",
            "Ingress events refused as unverified.");
    public static readonly OpsMetricSpec IngressUnmappedTotal =
        new("chorus_ingress_unmapped_total", OpsMetricKind.Counter, "count", "",
            "Ingress events refused as unmapped to a room.");
    public static readonly OpsMetricSpec IngressRateLimitedTotal =
        new("chorus_ingress_rate_limited_total", OpsMetricKind.Counter, "count", "",
            "Ingress events refused by the rate limiter.");
    public static readonly OpsMetricSpec IngressTooLargeTotal =
        new("chorus_ingress_too_large_total", OpsMetricKind.Counter, "count", "",
            "Ingress events refused as too large.");
    public static readonly OpsMetricSpec IngressTimedOutTotal =
        new("chorus_ingress_timed_out_total", OpsMetricKind.Counter, "count", "",
            "Ingress events refused as timed out.");
    public static readonly OpsMetricSpec InputEventsTotal =
        new("chorus_input_events_total", OpsMetricKind.Counter, "count", "source",
            "Raw platform input events, broken down by source (label: source).");

    // Event delivery and effect chain (OBS-11): the room-side event counts (already on
    // the session face) plus the control-plane outbox effect dispatch stages.
    public static readonly OpsMetricSpec EventsIngestedTotal =
        new("chorus_events_ingested_total", OpsMetricKind.Counter, "count", "",
            "Events ingested by the room sessions.");
    public static readonly OpsMetricSpec EventsAppliedTotal =
        new("chorus_events_applied_total", OpsMetricKind.Counter, "count", "",
            "Events applied by the room sessions.");
    public static readonly OpsMetricSpec EventsMergedTotal =
        new("chorus_events_merged_total", OpsMetricKind.Counter, "count", "",
            "Events merged into a pending batch.");
    public static readonly OpsMetricSpec EventsDroppedTotal =
        new("chorus_events_dropped_total", OpsMetricKind.Counter, "count", "",
            "Events dropped by the room sessions.");
    public static readonly OpsMetricSpec EventsDeliveredTotal =
        new("chorus_events_delivered_total", OpsMetricKind.Counter, "count", "",
            "Events delivered to their target room.");
    public static readonly OpsMetricSpec EventsEffectTotal =
        new("chorus_events_effect_total", OpsMetricKind.Counter, "count", "",
            "Events whose effect was applied at the target.");
    public static readonly OpsMetricSpec EffectsPending =
        new("chorus_effects_pending", OpsMetricKind.Gauge, "count", "",
            "Outbox effects currently waiting to dispatch.");
    public static readonly OpsMetricSpec EffectsDispatching =
        new("chorus_effects_dispatching", OpsMetricKind.Gauge, "count", "",
            "Outbox effects currently claimed for dispatch.");
    public static readonly OpsMetricSpec EffectsDone =
        new("chorus_effects_done", OpsMetricKind.Gauge, "count", "",
            "Outbox effects currently settled done.");
    public static readonly OpsMetricSpec EffectsFailedTotal =
        new("chorus_effects_failed_total", OpsMetricKind.Counter, "count", "",
            "Outbox effects that failed to deliver.");

    // Outbound platform API calls (OBS-12): endpoint is the bounded platform API name.
    public static readonly OpsMetricSpec PlatformApiCallsTotal =
        new("chorus_platform_api_calls_total", OpsMetricKind.Counter, "count", "endpoint",
            "Outbound platform API calls (label: endpoint).");
    public static readonly OpsMetricSpec PlatformApiFailuresTotal =
        new("chorus_platform_api_failures_total", OpsMetricKind.Counter, "count", "endpoint",
            "Outbound platform API calls that failed (label: endpoint).");
    public static readonly OpsMetricSpec PlatformApiLatencyMs =
        new("chorus_platform_api_latency_ms", OpsMetricKind.Histogram, "milliseconds", "endpoint",
            "Outbound platform API call latency (label: endpoint).");

    /// <summary>The whole dictionary. A downstream task amends this list — never adds a
    /// bare string literal beside it.</summary>
    public static readonly IReadOnlyList<OpsMetricSpec> All =
    [
        MachineCpuPercent, MachineMemoryUsedBytes, MachineMemoryTotalBytes, MachineMemoryPercent,
        MachineLoad1m, MachineLoad5m, MachineLoad15m,
        MachineDiskUsedBytes, MachineDiskTotalBytes, MachineDiskPercent, MachineUptimeSeconds,
        ProcessCpuPercent, ProcessMemoryUsedBytes, ProcessUptimeSeconds, ProcessesRunning,
        Rooms, RoomsByState, RoomsByTenant,
        OnlinePlayers, OnlineStreamers, OnlineRooms,
        IngressAcceptedTotal, IngressDuplicatesTotal, IngressUnverifiedTotal, IngressUnmappedTotal,
        IngressRateLimitedTotal, IngressTooLargeTotal, IngressTimedOutTotal, InputEventsTotal,
        EventsIngestedTotal, EventsAppliedTotal, EventsMergedTotal, EventsDroppedTotal,
        EventsDeliveredTotal, EventsEffectTotal,
        EffectsPending, EffectsDispatching, EffectsDone, EffectsFailedTotal,
        PlatformApiCallsTotal, PlatformApiFailuresTotal, PlatformApiLatencyMs,
    ];

    /// <summary>Looks a name up; an unknown name is a hard error, not a silent
    /// unregistered metric (a drifting literal is exactly what the dictionary is for).</summary>
    public static OpsMetricSpec Spec(string name) =>
        All.FirstOrDefault(spec => spec.Name == name)
        ?? throw new InvalidOperationException($"'{name}' is not in the ops metric dictionary (FIX-OBS-01).");
}

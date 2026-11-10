using Chorus.Rpc;

namespace Chorus.NodeAgent;

/// <summary>Maps a domain telemetry snapshot onto the wire contract (FIX-OBS-06): presence
/// flags separate "absent" from a real zero, and per-mount disk usage carries the bounded
/// mount label. Pure — the reporter builds the request, the control plane validates it.</summary>
public static class MachineTelemetryMapper
{
    public static MachineTelemetryRequest ToRequest(MachineTelemetrySample sample,
        string nodeId, string agentInstanceId, long reportSequence)
    {
        var request = new MachineTelemetryRequest
        {
            NodeId = nodeId,
            AgentInstanceId = agentInstanceId,
            SampledAtUnixMs = sample.SampledAtUnixMs,
            ReportSequence = reportSequence,
            CpuPresent = sample.CpuPercent is not null,
            CpuPercent = sample.CpuPercent ?? 0,
            MemoryPresent = sample.Memory is not null,
            MemoryTotalBytes = sample.Memory?.TotalBytes ?? 0,
            MemoryUsedBytes = sample.Memory?.UsedBytes ?? 0,
            MemoryUsedPercent = sample.Memory?.UsedPercent ?? 0,
            LoadPresent = sample.Load is not null,
            Load1M = sample.Load?.Load1m ?? 0,
            Load5M = sample.Load?.Load5m ?? 0,
            Load15M = sample.Load?.Load15m ?? 0,
        };
        if (sample.Disk is not null)
            foreach (var mount in sample.Disk)
                request.Disk.Add(new MachineDiskMountProto
                {
                    Mount = mount.Mount,
                    TotalBytes = mount.TotalBytes,
                    UsedBytes = mount.UsedBytes,
                    UsedPercent = mount.UsedPercent,
                });
        if (sample.Process is not null)
            foreach (var proc in sample.Process)
                request.Process.Add(new MachineProcessProto
                {
                    ProcessInstanceId = proc.ProcessInstanceId,
                    Role = proc.Role,
                    CpuPercent = proc.CpuPercent,
                    MemoryUsedBytes = proc.MemoryUsedBytes,
                    UptimeSeconds = proc.UptimeSeconds,
                });
        return request;
    }
}

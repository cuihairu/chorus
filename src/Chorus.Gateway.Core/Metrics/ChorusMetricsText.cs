using System.Text;
using Chorus.Engine.Session;
using Chorus.Gateway.Core.Faces;

namespace Chorus.Gateway.Core.Metrics;

/// <summary>
/// Renders the <c>chorus_*</c> Prometheus text exposition (review stage-3
/// observability). Deliberately dependency-free: hosts feed in their
/// diagnostics snapshots, this only formats.
/// </summary>
public static class ChorusMetricsText
{
    private const string ContentType = "text/plain; version=0.0.4; charset=utf-8";

    public static string ContentTypeValue => ContentType;

    /// <summary>Server (single-process) surface: session aggregates + both faces + IPC.</summary>
    public static string RenderServer(IReadOnlyList<SessionDiagnostics> sessions, FaceCounters faces, long ipcPublishFailures)
    {
        var sb = new StringBuilder();
        AppendFaceMetrics(sb, faces);

        sb.AppendLine("# TYPE chorus_ipc_publish_failures_total counter");
        sb.AppendLine($"chorus_ipc_publish_failures_total {ipcPublishFailures}");

        AppendSessionMetrics(sb, sessions);
        return sb.ToString();
    }

    /// <summary>EngineHost surface: session aggregates + worker process CPU when a sample exists.</summary>
    public static string RenderEngine(IReadOnlyList<SessionDiagnostics> sessions, double? processCpuPercent = null)
    {
        var sb = new StringBuilder();
        AppendSessionMetrics(sb, sessions);

        // Null = no previous scrape yet: omit the line rather than publish a
        // fake zero (todo §4 peak-measurement item).
        if (processCpuPercent is { } cpu)
        {
            sb.AppendLine("# TYPE chorus_process_cpu_percent gauge");
            sb.Append("chorus_process_cpu_percent ")
              .AppendLine(cpu.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture));
        }

        return sb.ToString();
    }

    /// <summary>Gateway surface: faces + IPC.</summary>
    public static string RenderGateway(FaceCounters faces, long ipcPublishFailures, long engineSilenceMs)
    {
        var sb = new StringBuilder();
        AppendFaceMetrics(sb, faces);
        sb.AppendLine("# TYPE chorus_ipc_publish_failures_total counter");
        sb.AppendLine($"chorus_ipc_publish_failures_total {ipcPublishFailures}");
        sb.AppendLine("# TYPE chorus_ipc_engine_silence_ms gauge");
        sb.AppendLine($"chorus_ipc_engine_silence_ms {engineSilenceMs}");
        return sb.ToString();
    }

    private static void AppendSessionMetrics(StringBuilder sb, IReadOnlyList<SessionDiagnostics> sessions)
    {
        long ingested = 0, applied = 0, merged = 0, dropped = 0, overflow = 0, gifts = 0;
        long snapshots = 0, resyncs = 0, overruns = 0, errors = 0, oversize = 0;
        long ackAccepted = 0, ackDuplicate = 0, ackStale = 0, ackInvalid = 0;
        var tickMsMax = 0L;
        var mailboxMax = 0;
        var ackLagMax = 0L;

        foreach (var s in sessions)
        {
            ingested += s.TotalIngestedEvents;
            applied += s.TotalAppliedEvents;
            merged += s.TotalMergedEvents;
            dropped += s.TotalDroppedEvents;
            overflow += s.TotalDroppedOverflow;
            gifts += s.TotalDroppedGifts;
            snapshots += s.TotalSnapshotsSent;
            resyncs += s.TotalResyncSnapshotsSent;
            overruns += s.TotalTickOverruns;
            errors += s.TotalTickErrors;
            oversize += s.TotalSnapshotsOversize;
            ackAccepted += s.TotalAcksAccepted;
            ackDuplicate += s.TotalAcksDuplicate;
            ackStale += s.TotalAcksStale;
            ackInvalid += s.TotalAcksInvalid;
            tickMsMax = Math.Max(tickMsMax, s.LastTickProcessMs);
            mailboxMax = Math.Max(mailboxMax, s.MailboxDepth);
            foreach (var c in s.Clients)
            {
                ackLagMax = Math.Max(ackLagMax, c.LagTicks);
            }
        }

        sb.AppendLine("# TYPE chorus_sessions gauge");
        sb.AppendLine($"chorus_sessions {sessions.Count}");

        AppendCounter(sb, "chorus_events_ingested_total", ingested);
        AppendCounter(sb, "chorus_events_applied_total", applied);
        AppendCounter(sb, "chorus_events_merged_total", merged);
        AppendCounter(sb, "chorus_events_dropped_total", dropped);
        AppendCounter(sb, "chorus_events_dropped_overflow_total", overflow);
        AppendCounter(sb, "chorus_events_dropped_gifts_total", gifts);
        AppendCounter(sb, "chorus_tick_overruns_total", overruns);
        AppendCounter(sb, "chorus_tick_errors_total", errors);
        AppendCounter(sb, "chorus_snapshots_sent_total", snapshots);
        AppendCounter(sb, "chorus_snapshots_resync_total", resyncs);
        AppendCounter(sb, "chorus_snapshots_oversize_total", oversize);
        AppendCounter(sb, "chorus_acks_accepted_total", ackAccepted);
        AppendCounter(sb, "chorus_acks_duplicate_total", ackDuplicate);
        AppendCounter(sb, "chorus_acks_stale_total", ackStale);
        AppendCounter(sb, "chorus_acks_invalid_total", ackInvalid);

        sb.AppendLine("# TYPE chorus_tick_duration_ms gauge");
        sb.AppendLine($"chorus_tick_duration_ms {tickMsMax}");
        sb.AppendLine("# TYPE chorus_mailbox_depth gauge");
        sb.AppendLine($"chorus_mailbox_depth {mailboxMax}");
        sb.AppendLine("# TYPE chorus_ack_lag_ticks gauge");
        sb.AppendLine($"chorus_ack_lag_ticks {ackLagMax}");
    }

    private static void AppendFaceMetrics(StringBuilder sb, FaceCounters faces)
    {
        var ingress = faces.Ingress;
        AppendCounter(sb, "chorus_ingress_accepted_total", ingress.Accepted);
        AppendCounter(sb, "chorus_ingress_duplicates_total", ingress.Duplicates);
        AppendCounter(sb, "chorus_ingress_unverified_total", ingress.Unverified);
        AppendCounter(sb, "chorus_ingress_unmapped_total", ingress.Unmapped);
        AppendCounter(sb, "chorus_ingress_rate_limited_total", ingress.RateLimited);
        AppendCounter(sb, "chorus_ingress_too_large_total", ingress.TooLarge);
        AppendCounter(sb, "chorus_ingress_timed_out_total", ingress.TimedOut);

        var ws = faces.StreamerWs;
        AppendCounter(sb, "chorus_ws_connections_total", ws.Connections);
        AppendCounter(sb, "chorus_ws_unauthorized_total", ws.Unauthorized);
        AppendCounter(sb, "chorus_ws_protocol_mismatch_total", ws.ProtocolMismatch);
        AppendCounter(sb, "chorus_ws_message_too_large_total", ws.MessageTooLarge);
        AppendCounter(sb, "chorus_ws_acks_accepted_total", ws.AckAccepted);
        AppendCounter(sb, "chorus_ws_acks_dropped_total", ws.AckDropped);
    }

    private static void AppendCounter(StringBuilder sb, string name, long value)
    {
        sb.AppendLine($"# TYPE {name} counter");
        sb.AppendLine($"{name} {value}");
    }
}

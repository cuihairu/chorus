using System.Text.RegularExpressions;
using Chorus.Rpc;
using Xunit;

namespace Chorus.Engine.Tests;

/// <summary>
/// The ops metric dictionary (FIX-OBS-01) as a closed contract: every name is a valid
/// unique Prometheus name under the chorus_ prefix, counters are the only names ending
/// _total, labels stay on bounded dimensions (never per-player or per-room IDs — the
/// architecture §12 cardinality rule), and the OBS-family names the stage-3 text face
/// already emits are all registered so the OBS tasks extend them instead of inventing
/// duplicates. The downstream OBS-02..12 tasks instrument against this dictionary.
/// </summary>
public sealed class RedesignObs01MetricsDictionaryTests
{
    [Fact]
    public void EveryNameIsAValidUniquePrometheusName()
    {
        var names = OpsMetrics.All.Select(spec => spec.Name).ToList();
        Assert.Equal(names.Count, names.Distinct(StringComparer.Ordinal).Count());
        var valid = new Regex(@"^chorus_[a-z][a-z0-9_]*$", RegexOptions.Compiled);
        foreach (var name in names)
        {
            Assert.Matches(valid, name);
        }
    }

    [Fact]
    public void CountersAreTheOnlyNamesEndingInTotal()
    {
        foreach (var spec in OpsMetrics.All)
        {
            var endsInTotal = spec.Name.EndsWith("_total", StringComparison.Ordinal);
            Assert.Equal(spec.Kind == OpsMetricKind.Counter, endsInTotal);
        }
    }

    [Fact]
    public void NoLabelIsAPerPlayerOrPerRoomId()
    {
        // The architecture §12 cardinality rule: metric labels never use per-player or
        // per-room IDs (nor the per-request/per-binding IDs that scale with traffic).
        // Bounded roster dimensions (tenant, game, state, mount, endpoint, source,
        // process_instance_id, role) are the only allowed labels.
        var forbidden = new HashSet<string>(StringComparer.Ordinal)
        {
            "player", "player_id", "playerid", "room", "room_id", "roomid",
            "request", "request_id", "requestid", "operation", "operation_id", "operationid",
            "trace", "trace_id", "binding", "binding_id", "generation", "epoch",
            "streamer", "streamer_id", "streamerid", "participant", "participant_id",
            "subject", "subject_id", "ticket", "ticket_id",
        };
        foreach (var spec in OpsMetrics.All)
        {
            foreach (var label in spec.Labels.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                Assert.False(forbidden.Contains(label),
                    $"{spec.Name} uses '{label}', a per-player/per-room/per-request ID label — the cardinality rule forbids it.");
            }
        }
    }

    [Fact]
    public void TheStage3FaceNamesAreAllRegistered()
    {
        // The names the stage-3 text renderer already emits for the OBS families: the
        // dictionary must cover them so OBS-10/11 extend, not duplicate. Adding a name
        // to the renderer without registering it here is a drift this fact catches.
        string[] existing =
        [
            "chorus_ingress_accepted_total", "chorus_ingress_duplicates_total",
            "chorus_ingress_unverified_total", "chorus_ingress_unmapped_total",
            "chorus_ingress_rate_limited_total", "chorus_ingress_too_large_total",
            "chorus_ingress_timed_out_total",
            "chorus_events_ingested_total", "chorus_events_applied_total",
            "chorus_events_merged_total", "chorus_events_dropped_total",
            "chorus_process_cpu_percent",
        ];
        foreach (var name in existing)
        {
            Assert.Equal(name, OpsMetrics.Spec(name).Name);
        }
    }

    [Fact]
    public void SpecThrowsForAnUnknownName()
    {
        Assert.Throws<InvalidOperationException>(() => OpsMetrics.Spec("chorus_never_defined"));
    }

    [Fact]
    public void EveryEntryCarriesADescription()
    {
        foreach (var spec in OpsMetrics.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(spec.Description),
                $"{spec.Name} has no description — a dictionary entry without a definition is not one.");
        }
    }
}

using System.Net;
using System.Text.RegularExpressions;
using Chorus.LoadTool;

var options = LoadToolOptions.Parse(args);

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

Console.WriteLine($"[loadtool] target={options.BaseUrl} ws={options.EffectiveWsUrl} platform={options.Platform} " +
                  $"sessions={options.Sessions} rate={options.EventsPerSecondTotal}/s total " +
                  $"clients={options.ClientsPerSession} duration={options.DurationSeconds}s");

if (string.IsNullOrWhiteSpace(options.SignSecret))
{
    // Upfront so an about-to-be-wasted run is visible before it burns.
    Console.Error.WriteLine(IngressDiagnostics.UnsignedRunWarning(options.Platform));
}

var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
var ingress = new IngressDriver(httpClient, options.BaseUrl, options.Platform, options);

var profiles = (options.AckMix.Length == 0 ? "fast,normal,slow" : options.AckMix)
    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

var wsUrl = options.EffectiveWsUrl;

var simulations = new List<ClientSimulation>();
var clientTasks = new List<Task>();
for (var s = 1; s <= Math.Max(1, options.Sessions); s++)
{
    var sessionId = $"load-s{s}";
    for (var c = 1; c <= Math.Max(1, options.ClientsPerSession); c++)
    {
        var clientId = $"load-s{s}-c{c}";
        var profile = profiles[(s + c) % profiles.Length];
        var sim = new ClientSimulation(wsUrl, sessionId, clientId, options.Token, profile,
            options.DisconnectEverySeconds, options.ReconnectBackoffMs, seed: s * 1000 + c);
        simulations.Add(sim);
        clientTasks.Add(sim.RunAsync(cts.Token));
    }
}

var ingressTask = ingress.RunAsync(cts.Token);
var peaks = new MetricsPeaks();
var reportTask = ReportLoopAsync(simulations, ingress, options, peaks, cts.Token);

try
{
    await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, options.DurationSeconds)), cts.Token);
    cts.Cancel();
}
catch (OperationCanceledException)
{
}

foreach (var task in clientTasks)
{
    await AwaitQuietly(task);
}
await AwaitQuietly(ingressTask);
await AwaitQuietly(reportTask);

PrintSummary(simulations, ingress, options);
PrintPeaks(peaks);
await PrintEngineMetricsAsync(httpClient, options);

// Fail loudly when a run sent events but the gateway accepted none: that is
// the pure-401 signature-failure shape (2026-10-15 run: ok=0, other=6316).
if (ingress.EventsSent > 0 && ingress.Accepted == 0)
{
    Console.Error.WriteLine(IngressDiagnostics.NothingAcceptedWarning(
        options.Platform, ingress.EventsSent, ingress.Unauthorized));
}

return IngressDiagnostics.ExitCodeFor(ingress.EventsSent, ingress.Accepted);

static async Task AwaitQuietly(Task task)
{
    try
    {
        await task;
    }
    catch (OperationCanceledException)
    {
    }
}

async Task ReportLoopAsync(IReadOnlyList<ClientSimulation> sims, IngressDriver ing, LoadToolOptions opts, MetricsPeaks peaks, CancellationToken ct)
{
    var tick = 0;
    while (!ct.IsCancellationRequested)
    {
        await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, opts.ReportEverySeconds)), ct);
        tick++;

        // Run-time sampling so the summary reports peaks, not just the final
        // scrape (todo §4 peak-measurement item). Best-effort: a failed scrape
        // never aborts the load run.
        if (!string.IsNullOrWhiteSpace(opts.EngineMetricsUrl))
        {
            try
            {
                peaks.Observe(await httpClient.GetStringAsync(opts.EngineMetricsUrl, ct));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
            }
            catch (Exception)
            {
            }
        }
        var snaps = sims.Sum(s => s.SnapshotsReceived);
        var acks = sims.Sum(s => s.AcksSent);
        var maxLag = sims.Count == 0 ? 0 : sims.Max(s => s.LastLagTicks);
        var reconnects = sims.Sum(s => s.Disconnects);
        Console.WriteLine(
            $"[loadtool] t={tick * Math.Max(1, opts.ReportEverySeconds)}s " +
            $"ingress(sent={ing.EventsSent},ok={ing.Accepted},dup={ing.Duplicated},401={ing.Unauthorized},429={ing.RateLimited},503={ing.LaneFull},err={ing.Other}) " +
            $"ws(snaps={snaps},acks={acks},maxLag={maxLag},reconnects={reconnects})");
    }
}

void PrintSummary(IReadOnlyList<ClientSimulation> sims, IngressDriver ing, LoadToolOptions opts)
{
    var reconnects = sims.Sum(s => s.Disconnects);
    var maxLag = sims.Count == 0 ? 0 : sims.Max(s => s.LastLagTicks);
    Console.WriteLine("[loadtool] done. ingress: " +
        $"sent={ing.EventsSent} accepted={ing.Accepted} duplicated={ing.Duplicated} " +
        $"unauthorized={ing.Unauthorized} rateLimited={ing.RateLimited} laneFull={ing.LaneFull} other={ing.Other}");
    Console.WriteLine("[loadtool] ws: " +
        $"snapshots={sims.Sum(s => s.SnapshotsReceived)} acks={sims.Sum(s => s.AcksSent)} " +
        $"resyncs={sims.Sum(s => s.ResyncsSent)} maxLagTicks={maxLag} reconnects={reconnects}");
}

void PrintPeaks(MetricsPeaks peaks)
{
    if (peaks.MaxSessions is null && peaks.MaxTickDurationMs is null && peaks.MaxCpuPercent is null)
    {
        return;
    }

    // Stage-3 trigger reads these peaks: "worker peak CPU > 60%" and
    // "room count in the hundreds" are countable from a sustained run now.
    Console.WriteLine("[loadtool] engine peaks: " +
        $"sessions={peaks.MaxSessions?.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) ?? "-"} " +
        $"tickMs={peaks.MaxTickDurationMs?.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) ?? "-"} " +
        $"cpuPercent={peaks.MaxCpuPercent?.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) ?? "-"}");
}

static async Task PrintEngineMetricsAsync(HttpClient http, LoadToolOptions opts)
{
    if (string.IsNullOrWhiteSpace(opts.EngineMetricsUrl))
    {
        return;
    }

    try
    {
        var text = await http.GetStringAsync(opts.EngineMetricsUrl);
        var wanted = Regex.Matches(text, @"^chorus_(sessions|events_applied_total|snapshots_sent_total|tick_overruns_total|tick_errors_total|mailbox_depth) .*$", RegexOptions.Multiline);
        Console.WriteLine("[loadtool] engine metrics:");
        foreach (Match m in wanted)
        {
            Console.WriteLine("[loadtool]   " + m.Value);
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[loadtool] engine metrics fetch failed: {ex.Message}");
    }
}

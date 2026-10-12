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

Console.WriteLine($"[loadtool] target={options.BaseUrl} platform={options.Platform} " +
                  $"sessions={options.Sessions} rate={options.EventsPerSecondPerSession}/s/session " +
                  $"clients={options.ClientsPerSession} duration={options.DurationSeconds}s");

var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
var ingress = new IngressDriver(httpClient, options.BaseUrl, options.Platform, options);

var profiles = (options.AckMix.Length == 0 ? "fast,normal,slow" : options.AckMix)
    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

var wsUrl = options.BaseUrl.TrimEnd('/') switch
{
    var u when u.StartsWith("https:", StringComparison.OrdinalIgnoreCase) => "wss://" + u[8..] + "/ws",
    var u when u.StartsWith("http:", StringComparison.OrdinalIgnoreCase) => "ws://" + u[7..] + "/ws",
    var u => "ws://" + u + "/ws",
};

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
var reportTask = ReportLoopAsync(simulations, ingress, options, cts.Token);

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
await PrintEngineMetricsAsync(httpClient, options);

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

async Task ReportLoopAsync(IReadOnlyList<ClientSimulation> sims, IngressDriver ing, LoadToolOptions opts, CancellationToken ct)
{
    var tick = 0;
    while (!ct.IsCancellationRequested)
    {
        await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, opts.ReportEverySeconds)), ct);
        tick++;
        var snaps = sims.Sum(s => s.SnapshotsReceived);
        var acks = sims.Sum(s => s.AcksSent);
        var maxLag = sims.Count == 0 ? 0 : sims.Max(s => s.LastLagTicks);
        var reconnects = sims.Sum(s => s.Disconnects);
        Console.WriteLine(
            $"[loadtool] t={tick * Math.Max(1, opts.ReportEverySeconds)}s " +
            $"ingress(sent={ing.EventsSent},ok={ing.Accepted},dup={ing.Duplicated},429={ing.RateLimited},503={ing.LaneFull},err={ing.Other}) " +
            $"ws(snaps={snaps},acks={acks},maxLag={maxLag},reconnects={reconnects})");
    }
}

void PrintSummary(IReadOnlyList<ClientSimulation> sims, IngressDriver ing, LoadToolOptions opts)
{
    var reconnects = sims.Sum(s => s.Disconnects);
    var maxLag = sims.Count == 0 ? 0 : sims.Max(s => s.LastLagTicks);
    Console.WriteLine("[loadtool] done. ingress: " +
        $"sent={ing.EventsSent} accepted={ing.Accepted} duplicated={ing.Duplicated} " +
        $"rateLimited={ing.RateLimited} laneFull={ing.LaneFull} other={ing.Other}");
    Console.WriteLine("[loadtool] ws: " +
        $"snapshots={sims.Sum(s => s.SnapshotsReceived)} acks={sims.Sum(s => s.AcksSent)} " +
        $"maxLagTicks={maxLag} reconnects={reconnects}");
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

using System.Net;
using System.Net.Sockets;
using System.Text;
using Chorus.Control;
using Chorus.ControlHost;
using Xunit;

namespace Chorus.Engine.Tests;

/// <summary>
/// The BOTS-3 pressure panel's moving parts: the hot-frame reader ranks a speedscope's
/// leaf self time; the fleet service answers an operator's mistakes at the request
/// (relative or non-HTTP endpoints, zero rates, missing root material); one live run
/// at a time (a second start conflicts with 409 semantics); a dead ingress endpoint
/// still completes with every send counted as other; a stop while the run lives names
/// the run Stopped; and the whole one-click capture chain — collect window over the
/// driver's own process, conversion to speedscope, hot frames onto the record — runs
/// end to end against the same stub-tool discipline the load tool's own tests use
/// (the real dotnet-trace is not a test dependency).
/// </summary>
public sealed class SimulatorPressurePanelTests : IDisposable
{
    private readonly string _work = Path.Combine(Path.GetTempPath(), "chorus-pressure-" + Guid.NewGuid().ToString("N"));

    public SimulatorPressurePanelTests() => Directory.CreateDirectory(_work);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_work, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static PressureStart Start(string baseUrl, int durationSeconds = 1, int profileSeconds = 0) => new(
        BaseUrl: baseUrl, Platform: "douyin", EventsPerSecond: 10, Sessions: 1,
        ProducerConnections: 1, DurationSeconds: durationSeconds, ProfileSeconds: profileSeconds);

    private static async Task<PressureRunRecord?> AwaitTerminal(PressureFleetService service, string id, TimeSpan budget)
    {
        var deadline = DateTime.UtcNow + budget;
        while (DateTime.UtcNow < deadline)
        {
            var run = service.Run(id);
            if (PressureFleetService.Terminal(run.State)) return run;
            await Task.Delay(100);
        }
        return null;
    }

    // The synthetic speedscope: frames 0/1/2 with sample weights 1,1,1,1,1,2. Every
    // sample's leaf owns its weight, so frame 2 owns 6 of 7 units (85.7%) and frame 0
    // owns 1 (14.3%); frame 1 is always interior and owns nothing. The second profile
    // is evented — it carries no self time and must be skipped.
    private static readonly string ModelSpeedscope = """
        {
          "shared": { "frames": [ { "name": "Run" }, { "name": "SendAsync" }, { "name": "HandleIngress" } ] },
          "profiles": [
            { "type": "sampled", "samples": [ [0,1,2], [0,2], [1,2], [2,0], [2], [0,1,2] ], "weights": [1,1,1,1,1,2] },
            { "type": "evented", "events": [ { "frame": 2, "at": 0.1 } ] }
          ]
        }
        """;

    [Fact]
    public void SpeedscopeParseRanksLeafSelfTimeAndSkipsEventedProfiles()
    {
        var path = Path.Combine(_work, "model.speedscope.json");
        File.WriteAllText(path, ModelSpeedscope);

        var hot = PressureFlame.Parse(path);

        Assert.Equal(2, hot.Count);
        Assert.Equal("HandleIngress", hot[0].Frame);
        Assert.Equal(85.7, hot[0].SelfSharePercent);
        Assert.Equal("Run", hot[1].Frame);
        Assert.Equal(14.3, hot[1].SelfSharePercent);

        // The cap keeps the panel list short; an interior frame never appears.
        var only = PressureFlame.Parse(path, count: 1);
        Assert.Single(only);
        Assert.Equal("HandleIngress", only[0].Frame);
    }

    [Fact]
    public void SpeedscopeParseDefaultsMissingWeightsToOne()
    {
        var path = Path.Combine(_work, "unweighted.speedscope.json");
        File.WriteAllText(path, """
            {
              "shared": { "frames": [ { "name": "a" }, { "name": "b" } ] },
              "profiles": [ { "type": "sampled", "samples": [ [0,1], [1] ] } ]
            }
            """);

        var hot = PressureFlame.Parse(path);

        // Without weights every sample is one unit: leaf 1 owns two, leaf 0 none.
        Assert.Single(hot);
        Assert.Equal("b", hot[0].Frame);
        Assert.Equal(100, hot[0].SelfSharePercent);
    }

    [Fact]
    public void FleetRejectsBadRequestsAtTheRequest()
    {
        var service = new PressureFleetService();
        Rejects(service, () => Start("127.0.0.1:9"), "baseUrl");
        Rejects(service, () => Start("ftp://127.0.0.1:9/"), "baseUrl");
        Rejects(service, () => Start("http://127.0.0.1:9/") with { Platform = " " }, "platform");
        Rejects(service, () => Start("http://127.0.0.1:9/") with { EventsPerSecond = 0 }, "eventsPerSecond");
        Rejects(service, () => Start("http://127.0.0.1:9/") with { Sessions = 0 }, "sessions");
        Rejects(service, () => Start("http://127.0.0.1:9/") with { ProducerConnections = 0 }, "producerConnections");
        Rejects(service, () => Start("http://127.0.0.1:9/") with { DurationSeconds = 0 }, "durationSeconds");
        Rejects(service, () => Start("http://127.0.0.1:9/", profileSeconds: -1), "profileSeconds");
        Rejects(service, () => Start("http://127.0.0.1:9/") with { RootCaPemPath = "/nonexistent/root.pem" }, "rootCaPemPath");
    }

    private static void Rejects(PressureFleetService service, Func<PressureStart> start, string field)
    {
        var thrown = Assert.Throws<ArgumentException>(() => service.Start(start()));
        Assert.Contains(field, thrown.Message);
    }

    [Fact]
    public async Task DeadIngressEndpointStillCompletesWithEverySendCountedOther()
    {
        var service = new PressureFleetService();

        var started = service.Start(Start("http://127.0.0.1:9/"));

        var run = await AwaitTerminal(service, started.Id, TimeSpan.FromSeconds(20));
        Assert.NotNull(run);
        Assert.Equal("Completed", run!.State);
        Assert.Null(run.Error);
        // A refused endpoint is the driver's plain other: nothing crashed, the run
        // still names itself done, and every attempted send landed in other.
        Assert.NotNull(run.Sample);
        Assert.True(run.Sample!.Sent > 0, $"sent={run.Sample.Sent}");
        Assert.Equal(run.Sample.Sent, run.Sample.Other);
        Assert.Equal(0, run.Sample.Accepted);
    }

    [Fact]
    public async Task OneLiveRunConflictsAndAStopNamesItStopped()
    {
        // A listener that never accepts keeps HTTP sends waiting on the backlog —
        // the deterministic stand-in for a wedged ingress face.
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var service = new PressureFleetService();

        var first = service.Start(Start($"http://127.0.0.1:{port}/", durationSeconds: 60));
        var second = Assert.Throws<ControlConflictException>(() => service.Start(Start($"http://127.0.0.1:{port}/")));
        Assert.Contains(first.Id, second.Message);

        Assert.True(service.Stop(first.Id));
        var stopped = await AwaitTerminal(service, first.Id, TimeSpan.FromSeconds(15));
        Assert.NotNull(stopped);
        Assert.True(stopped!.State == "Stopped", $"{stopped.State}: {stopped.Error}");
        // Terminal answers false — there is nothing left to stop.
        Assert.False(service.Stop(first.Id));

        // The slot frees with the run: a new start goes through and stops too.
        var third = service.Start(Start($"http://127.0.0.1:{port}/", durationSeconds: 60));
        Assert.True(service.Stop(third.Id));
        var thirdStopped = await AwaitTerminal(service, third.Id, TimeSpan.FromSeconds(15));
        Assert.NotNull(thirdStopped);
        Assert.True(thirdStopped!.State == "Stopped", $"{thirdStopped.State}: {thirdStopped.Error}");
        listener.Stop();
    }

    [Fact]
    public void UnknownPressureRunAnswersNotFound()
    {
        var service = new PressureFleetService();
        Assert.Throws<KeyNotFoundException>(() => service.Stop("pressure-run-404"));
        Assert.Throws<KeyNotFoundException>(() => service.Run("pressure-run-404"));
    }

    [Fact]
    public async Task ProfileWindowRunsTheCaptureChainEndToEnd()
    {
        var model = Path.Combine(_work, "model.speedscope.json");
        File.WriteAllText(model, ModelSpeedscope);
        var flameDir = Path.Combine(_work, "flames");
        var stubLog = Path.Combine(_work, "stub-args.log");
        var stub = WriteStubTool("""
            #!/bin/sh
            echo "$@" >> "$PRESSURE_STUB_LOG"
            out=""
            prev=""
            for a in "$@"; do
              if [ "$prev" = "-o" ]; then out="$a"; fi
              prev="$a"
            done
            if [ "$1" = "collect" ]; then
              : > "$out"
            elif [ "$1" = "convert" ]; then
              cat "$PRESSURE_STUB_MODEL" > "${2%.nettrace}.speedscope.json"
            fi
            """);
        var previousLog = Environment.GetEnvironmentVariable("PRESSURE_STUB_LOG");
        var previousModel = Environment.GetEnvironmentVariable("PRESSURE_STUB_MODEL");
        Environment.SetEnvironmentVariable("PRESSURE_STUB_LOG", stubLog);
        Environment.SetEnvironmentVariable("PRESSURE_STUB_MODEL", model);
        try
        {
            var service = new PressureFleetService(() => stub, flameGraphDir: flameDir);

            // The capture window cannot outlive the run it profiles: 5s window over
            // a 2s run is a 2s trace, and a dead endpoint keeps the run honest.
            var started = service.Start(
                Start("http://127.0.0.1:9/", durationSeconds: 2, profileSeconds: 5));

            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
            while (DateTime.UtcNow < deadline)
            {
                var run = service.Run(started.Id);
                if (run.SpeedscopePath is not null && run.HotFrames.Count > 0) break;
                await Task.Delay(200);
            }
            var finished = service.Run(started.Id);
            Assert.True(PressureFleetService.Terminal(finished.State), $"{finished.State}: {finished.Error}");
            Assert.Equal("Completed", finished.State);
            Assert.NotNull(finished.TracePath);
            Assert.True(finished.TracePath!.StartsWith(Path.Combine(flameDir, "pressure_pressure-run-"), StringComparison.Ordinal),
                finished.TracePath);
            Assert.True(File.Exists(finished.SpeedscopePath), finished.SpeedscopePath);

            // The hottest frames came off the speedscope the stub converted.
            Assert.Equal(2, finished.HotFrames.Count);
            Assert.Equal("HandleIngress", finished.HotFrames[0].Frame);
            Assert.Equal(85.7, finished.HotFrames[0].SelfSharePercent);
            Assert.Equal("Run", finished.HotFrames[1].Frame);
            Assert.Equal(14.3, finished.HotFrames[1].SelfSharePercent);

            // The collect argv pins the panel seam: the driver's own process, the
            // window capped at the run's duration, into the panel's trace path.
            var argv = await File.ReadAllLinesAsync(stubLog);
            var collect = argv.Single(line => line.StartsWith("collect", StringComparison.Ordinal));
            Assert.Contains($"--process-id {Environment.ProcessId}", collect);
            Assert.Contains("--duration 2", collect);
            Assert.Contains("--profile cpu-sampling", collect);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PRESSURE_STUB_LOG", previousLog);
            Environment.SetEnvironmentVariable("PRESSURE_STUB_MODEL", previousModel);
        }
    }

    private string WriteStubTool(string body)
    {
        var path = Path.Combine(_work, "dotnet-trace-stub.sh");
        File.WriteAllText(path, body, new UTF8Encoding(false));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        return path;
    }
}

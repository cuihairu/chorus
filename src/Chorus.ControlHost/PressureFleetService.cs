using System.Globalization;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Chorus.Control;
using Chorus.LoadTool;

namespace Chorus.ControlHost;

/// <summary>One panel-started pressure run: where to drive (the ingress face), at what
/// rate and shape, for how long — and the optional bounded flame-graph window taken
/// while the run is live. ProfileSeconds 0 means no capture; ProfileProcessId 0 means
/// this control host's own process (where the drivers run); an operator who knows the
/// gateway's pid names it instead.</summary>
public sealed record PressureStart(
    string BaseUrl, string Platform, int EventsPerSecond, int Sessions,
    int ProducerConnections, int DurationSeconds, string? SignSecret = null,
    int ProfileSeconds = 0, int ProfileProcessId = 0, string RootCaPemPath = "");

/// <summary>The driver's own running totals for the panel: per-outcome counters plus
/// the target rate and the rate the run is actually achieving.</summary>
public sealed record PressureSample(
    long Sent, long Accepted, long Duplicated, long RateLimited, long LaneFull,
    long Unauthorized, long Other, long TargetPerSecond, double ElapsedSeconds,
    double AchievedPerSecond);

/// <summary>One frame's self-time share of the profile, as the panel ranks them.</summary>
public sealed record HotFrame(string Frame, double SelfSharePercent);

/// <summary>One pressure run as the panel sees it: the lifecycle state, the running
/// sample, the failure if it died, and — once the capture window finishes and the
/// conversion lands — the speedscope path and the hottest self-time frames.</summary>
public sealed record PressureRunRecord(
    string Id, string State, PressureSample? Sample,
    DateTimeOffset? StartedAt, DateTimeOffset? FinishedAt, string? Error,
    string? TracePath, string? SpeedscopePath, IReadOnlyList<HotFrame> HotFrames);

/// <summary>
/// Reads a speedscope file's hottest self-time frames (BOTS-3): every sampled profile's
/// leaf frame owns its sample's weight, so the top leaves are where the wall clock went.
/// Evented profiles (no samples array) carry no self time and are skipped.
/// </summary>
public static class PressureFlame
{
    public static IReadOnlyList<HotFrame> Parse(string speedscopePath, int count = 10)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(speedscopePath));
        var frames = document.RootElement.GetProperty("shared").GetProperty("frames");
        var names = frames.EnumerateArray()
            .Select(frame => frame.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String
                ? name.GetString() ?? "" : "")
            .ToArray();
        var self = new double[names.Length];
        double total = 0;
        foreach (var profile in document.RootElement.GetProperty("profiles").EnumerateArray())
        {
            if (!profile.TryGetProperty("samples", out var samples)
                || samples.ValueKind != JsonValueKind.Array)
                continue;
            JsonElement weights = profile.TryGetProperty("weights", out var found) ? found : default;
            var weighted = weights.ValueKind == JsonValueKind.Array;
            for (var i = 0; i < samples.GetArrayLength(); i++)
            {
                var stack = samples[i];
                if (stack.GetArrayLength() == 0) continue;
                var leaf = stack[stack.GetArrayLength() - 1].GetInt32();
                var weight = weighted ? weights[i].GetDouble() : 1;
                if (leaf >= 0 && leaf < self.Length) self[leaf] += weight;
                total += weight;
            }
        }
        if (total <= 0) return [];
        return Enumerable.Range(0, self.Length)
            .Where(i => self[i] > 0)
            .OrderByDescending(i => self[i])
            .Take(count)
            .Select(i => new HotFrame(names[i], Math.Round(self[i] * 100.0 / total, 1)))
            .ToList();
    }
}

/// <summary>
/// The panel's pressure fleet (BOTS-3): drives the ingress face with the load tool's own
/// driver (same IngressDriver, same RatePacer absolute schedule — no second driver
/// semantics), one run at a time so a reading is never polluted by a second source, and
/// optionally captures a bounded dotnet-trace window over the run. Event ids draw from a
/// per-run prefix, so one run's dedup keys never collide with the next.
/// </summary>
public sealed class PressureFleetService(Func<string?>? toolPathProvider = null, string flameGraphDir = "flamegraphs")
{
    private readonly object _lock = new();
    private readonly Dictionary<string, RunState> _runs = [];
    private long _sequence;

    private sealed class RunState
    {
        public required string Id;
        public long Number;
        public required PressureStart Start;
        public required CancellationTokenSource Cancellation;
        public IngressDriver? Driver;
        public string State = "Starting";
        public DateTimeOffset? StartedAt;
        public DateTimeOffset? FinishedAt;
        public string? Error;
        public PressureSample? Sample;
        public string? TracePath;
        public string? SpeedscopePath;
        public IReadOnlyList<HotFrame> HotFrames = [];

        public void Begin()
        {
            lock (this)
            {
                State = "Running";
                StartedAt = DateTimeOffset.UtcNow;
            }
        }

        public void Hold(string state, string? error)
        {
            lock (this)
            {
                State = state;
                Error = error;
                FinishedAt = DateTimeOffset.UtcNow;
                Sample = SampleLocked();
            }
        }

        public void SetProfile(string? tracePath, string? speedscopePath, IReadOnlyList<HotFrame> hotFrames)
        {
            lock (this)
            {
                TracePath = tracePath;
                SpeedscopePath = speedscopePath;
                HotFrames = hotFrames;
            }
        }

        public PressureRunRecord Record()
        {
            lock (this)
            {
                // Live runs read the driver's Interlocked counters on demand; terminal
                // runs show the numbers frozen at the Hold that ended them.
                var sample = PressureFleetService.Terminal(State) ? Sample : SampleLocked();
                return new PressureRunRecord(Id, State, sample, StartedAt, FinishedAt, Error,
                    TracePath, SpeedscopePath, HotFrames);
            }
        }

        private PressureSample? SampleLocked()
        {
            if (Driver is not { } driver) return null;
            var elapsed = StartedAt is { } started
                ? Math.Max(0.1, (DateTimeOffset.UtcNow - started).TotalSeconds) : 0.1;
            var sent = driver.EventsSent;
            return new PressureSample(sent, driver.Accepted, driver.Duplicated,
                driver.RateLimited, driver.LaneFull, driver.Unauthorized, driver.Other,
                Start.EventsPerSecond, Math.Round(elapsed, 1), Math.Round(sent / elapsed, 1));
        }
    }

    /// <summary>Starts one pressure run. Answers 400 (ArgumentException) for a bad
    /// request, 409 (ControlConflictException) while another run is still live.</summary>
    public PressureRunRecord Start(PressureStart start)
    {
        if (!Uri.TryCreate(start.BaseUrl, UriKind.Absolute, out var baseUri)
            || baseUri.Scheme is not ("http" or "https"))
            throw new ArgumentException("baseUrl: an absolute http(s) address is required");
        if (string.IsNullOrWhiteSpace(start.Platform))
            throw new ArgumentException("platform: the ingress platform name is required");
        if (start.EventsPerSecond < 1)
            throw new ArgumentException("eventsPerSecond: at least one event per second");
        if (start.Sessions < 1)
            throw new ArgumentException("sessions: at least one session");
        if (start.ProducerConnections < 1)
            throw new ArgumentException("producerConnections: at least one producer");
        if (start.DurationSeconds < 1)
            throw new ArgumentException("durationSeconds: at least one second");
        if (start.ProfileSeconds < 0)
            throw new ArgumentException("profileSeconds: zero (off) or a positive window");
        if (!string.IsNullOrWhiteSpace(start.RootCaPemPath) && !File.Exists(start.RootCaPemPath))
            throw new ArgumentException($"rootCaPemPath: no file at {start.RootCaPemPath}");

        var run = new RunState
        {
            Id = "", Start = start, Cancellation = new CancellationTokenSource()
        };
        lock (_lock)
        {
            if (_runs.Values.Any(r => !Terminal(r.State)))
                throw new ControlConflictException(
                    $"Pressure run {_runs.Values.First(r => !Terminal(r.State)).Id} is still live");
            run.Number = ++_sequence;
            run.Id = "pressure-run-" + run.Number;
            _runs[run.Id] = run;
            TrimLocked();
        }
        _ = Task.Run(() => ExecuteAsync(run));
        return run.Record();
    }

    /// <summary>Cancels a live run. Unknown ids answer 404; terminal runs answer false.</summary>
    public bool Stop(string id)
    {
        lock (_lock)
        {
            if (!_runs.TryGetValue(id, out var run))
                throw new KeyNotFoundException("No pressure run " + id);
            if (Terminal(run.State)) return false;
            run.Cancellation.Cancel();
            return true;
        }
    }

    public IReadOnlyList<PressureRunRecord> Runs()
    {
        lock (_lock)
            return _runs.Values.OrderByDescending(r => r.Number).Select(r => r.Record()).ToList();
    }

    public PressureRunRecord Run(string id)
    {
        lock (_lock)
            return _runs.TryGetValue(id, out var run)
                ? run.Record()
                : throw new KeyNotFoundException("No pressure run " + id);
    }

    public static bool Terminal(string state) =>
        state is "Completed" or "Stopped" or "Failed";

    private async Task ExecuteAsync(RunState run)
    {
        FlameGraphProfiler? profiler = null;
        HttpClient? http = null;
        X509Certificate2? root = null;
        // The window's own end is a natural completion; the operator's stop is a
        // stop — the producers' pacing delay surfaces cancellation as OCE either
        // way, so the run keeps the flag that names which one happened.
        var elapsed = false;
        try
        {
            if (run.Start.ProfileSeconds > 0)
            {
                // The panel seam starts a capture into an explicit path: the trace
                // directory is this service's to make, the way the options overload
                // makes its own before the first trace lands there.
                Directory.CreateDirectory(flameGraphDir);
                var window = Math.Min(run.Start.ProfileSeconds, run.Start.DurationSeconds);
                var tracePath = Path.Combine(flameGraphDir,
                    $"pressure_{run.Id}_{DateTimeOffset.UtcNow.ToString("yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture)}.nettrace");
                profiler = FlameGraphProfiler.TryStart(window, tracePath,
                    run.Start.ProfileProcessId > 0 ? run.Start.ProfileProcessId : Environment.ProcessId,
                    toolPathProvider?.Invoke() ?? FlameGraphProfiler.ResolveToolPath());
            }

            HttpClientHandler handler;
            if (string.IsNullOrWhiteSpace(run.Start.RootCaPemPath))
            {
                handler = new HttpClientHandler();
            }
            else
            {
                // The same CustomRootTrust rule the internal ingress clients dial with:
                // the operator's root validates the face's leaf, nothing else.
                root = X509Certificate2.CreateFromPem(File.ReadAllText(run.Start.RootCaPemPath));
                handler = new HttpClientHandler
                {
                    ServerCertificateCustomValidationCallback = (_, certificate, _, _) =>
                    {
                        if (certificate is null || root is null) return false;
                        using var candidate = new X509Certificate2(certificate);
                        using var validator = new X509Chain();
                        validator.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                        validator.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                        validator.ChainPolicy.CustomTrustStore.Add(root);
                        return validator.Build(candidate);
                    }
                };
            }
            http = new HttpClient(handler);

            var options = new LoadToolOptions(
                BaseUrl: run.Start.BaseUrl, Platform: run.Start.Platform,
                Sessions: run.Start.Sessions, EventsPerSecondTotal: run.Start.EventsPerSecond,
                ProducerConnections: run.Start.ProducerConnections,
                DurationSeconds: run.Start.DurationSeconds,
                SignSecret: string.IsNullOrWhiteSpace(run.Start.SignSecret) ? null : run.Start.SignSecret,
                EventNamespace: run.Id);
            var driver = new IngressDriver(http, run.Start.BaseUrl, run.Start.Platform, options);
            run.Driver = driver;
            run.Begin();

            // The producers loop until they are cancelled — the bounded window is the
            // run's clock. When it elapses the run is over; an operator's stop beats
            // the clock any time and must never be misread as a natural completion.
            var driving = driver.RunAsync(run.Cancellation.Token);
            var clock = Task.Delay(TimeSpan.FromSeconds(run.Start.DurationSeconds), run.Cancellation.Token);
            var finished = await Task.WhenAny(driving, clock);
            if (finished == clock && !clock.IsCanceled)
            {
                elapsed = true;
                run.Cancellation.Cancel();
            }
            await driving;
            run.Hold(elapsed ? "Completed" : run.Cancellation.IsCancellationRequested ? "Stopped" : "Completed", null);
        }
        catch (OperationCanceledException) when (run.Cancellation.IsCancellationRequested)
        {
            run.Hold(elapsed ? "Completed" : "Stopped", null);
        }
        catch (Exception ex)
        {
            run.Hold("Failed", ex.Message);
        }
        finally
        {
            http?.Dispose();
            root?.Dispose();
        }

        if (profiler is not null)
        {
            // The capture is best-effort to its bones: a missing tool, a failed
            // collect, a failed convert or an unreadable speedscope is guidance on
            // stderr, never a failed run. The speedscope path still shows even when
            // the hot-frame read fails — the operator can open it by hand.
            await profiler.CompleteAsync(Console.Error);
            if (File.Exists(profiler.SpeedscopePath))
            {
                try
                {
                    run.SetProfile(profiler.TracePath, profiler.SpeedscopePath,
                        PressureFlame.Parse(profiler.SpeedscopePath));
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine(
                        $"[pressure] hot-frame read failed ({ex.Message}); the speedscope file stays where it is.");
                    run.SetProfile(profiler.TracePath, profiler.SpeedscopePath, []);
                }
            }
            else
                run.SetProfile(profiler.TracePath, null, []);
        }
    }

    /// <summary>Bounded history like the bots fleet: past the cap the oldest terminal
    /// runs fall off; the one live run never leaves.</summary>
    private void TrimLocked()
    {
        if (_runs.Count <= 200) return;
        foreach (var id in _runs.Values.Where(r => Terminal(r.State)).OrderBy(r => r.Number)
                     .Take(_runs.Count - 200).Select(r => r.Id).ToList())
            _runs.Remove(id);
    }
}

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Chorus.Rpc;
using Chorus.StreamerGateway;
using Chorus.StreamerGateway.Messages;
using Xunit;
using Xunit.Abstractions;

namespace Chorus.Engine.Tests;

/// <summary>
/// FIX-LINK-11's in-suite gate: the gateway fanout+outbox segment must carry the §11 frame
/// channel budget with room to spare. The unit shape runs 20 rooms x 2 seats at 3x the §11
/// aggregate (6000 frames/s) and asserts the delivered rate, zero unintended outbox drops,
/// and a dispatch-to-transport latency well inside the tick budget. It always runs — it is
/// the acceptance, not an opt-in benchmark — and it reads its budgets from the explicit
/// frame-channel-acceptance.json so the gate is reviewable without recompiling.
///
/// The workload is the honest shape the budget model names (architecture.md §11): ~4 KiB
/// frames committed across live rooms. The producer is the M2 deficit/catch-up limiter so
/// the delivered rate tracks the dispatch ceiling instead of a bare Task.Delay beat — a
/// slow fanout shows up as a shortfall, not as noise. Each subscription offsets its ticks
/// by 1_000_000 so a re-baseline replay can never alias a (roomId, tick) latency key.
/// </summary>
public sealed class RedesignFrameChannelCapacityUnitTests
{
    private readonly ITestOutputHelper _output;

    public RedesignFrameChannelCapacityUnitTests(ITestOutputHelper output) => _output = output;

    /// <summary>The main acceptance fact: the fanout+outbox segment sustains the declared
    /// frame-channel budget. Every budget is a named reason in the report; a breach fails.</summary>
    [Fact]
    public async Task TheGatewayFanoutSustainsTheFrameChannelCapacityBudget()
    {
        var config = FrameChannelAcceptance.LoadConfig();
        var started = DateTime.UtcNow;
        var measurement = await RunAsync(config.Unit);
        var report = FrameChannelAcceptance.Evaluate(measurement, config.Unit, started, DateTime.UtcNow);
        _output.WriteLine(report.Describe());
        Assert.True(report.Verdict == FrameChannelVerdict.Passed, report.Describe());
    }

    /// <summary>Runs the R x S workload against the fanout and measures what a client would:
    /// how many frames reached the transports, how long dispatch took, and whether the send
    /// queue ever refused or superseded a frame (the single backpressure mechanism).</summary>
    private static async Task<FrameChannelMeasurement> RunAsync(FrameChannelUnitConfig config)
    {
        var perSecondPerRoom = config.TargetAggregateFrameRatePerSecond / (double)config.Rooms;
        var totalFrames = (int)Math.Ceiling(perSecondPerRoom * config.RunSeconds);
        var publishStamps = new ConcurrentDictionary<(string RoomId, long TickId), long>();
        var counts = new FrameCounters();
        var gates = new TaskCompletionSource<bool>[config.Rooms];
        for (var i = 0; i < config.Rooms; i++)
            gates[i] = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var sync = new ScriptedSync();
        for (var i = 0; i < config.Rooms; i++)
        {
            var gate = gates[i];
            sync.Stream((request, subscriptionIndex, cancellation) => RateLimitedDeltas(
                request.RoomId, subscriptionIndex, perSecondPerRoom, config.FramePayloadBytes,
                totalFrames, gate, publishStamps, cancellation));
        }

        var fanout = new EdgeRoomFanout(sync.Subscribe)
        {
            ResolveRoom = (_, _) => Task.FromResult<EdgeRoomFanout.ResolvedRoom?>(null),
            StreamClosed = _ => { }
        };

        var seats = new List<TimedConnection>();
        try
        {
            for (var room = 0; room < config.Rooms; room++)
            {
                var roomId = $"room-{room}";
                for (var seat = 0; seat < config.SeatsPerRoom; seat++)
                {
                    var connection = new TimedConnection($"seat-{room}-{seat}", counts);
                    seats.Add(connection);
                    fanout.Join(roomId, "game-1", "ep-1", 1, "control", connection.Connection);
                }
            }

            var startStamp = Stopwatch.GetTimestamp();
            foreach (var gate in gates) gate.SetResult(true);

            var expectedRaw = (long)config.Rooms * config.SeatsPerRoom * (totalFrames + 1);
            var deadline = Environment.TickCount64
                + (int)(config.RunSeconds * 2000) + 20_000;
            while (Environment.TickCount64 < deadline)
            {
                if (Volatile.Read(ref counts.Raw) >= expectedRaw) break;
                await Task.Delay(20, CancellationToken.None);
            }
            var endStamp = seats.Count == 0 ? startStamp
                : seats.Max(seat => seat.LastReceiptStamp);

            foreach (var seat in seats) fanout.Leave(seat.Connection.ConnectionId);
            foreach (var seat in seats) seat.Dispose();

            return BuildMeasurement(config, seats, publishStamps, startStamp, endStamp, totalFrames);
        }
        catch (Exception error)
        {
            foreach (var seat in seats)
            {
                try { fanout.Leave(seat.Connection.ConnectionId); } catch { }
                try { seat.Dispose(); } catch { }
            }
            return new FrameChannelMeasurement(config.Rooms, config.SeatsPerRoom, 0, 0, 0, 0, 0, 0, 0,
                error.Message);
        }
    }

    private static FrameChannelMeasurement BuildMeasurement(
        FrameChannelUnitConfig config, List<TimedConnection> seats,
        ConcurrentDictionary<(string RoomId, long TickId), long> publishStamps,
        long startStamp, long endStamp, int totalFrames)
    {
        long deliveredDeltas = 0;
        long unkeyed = 0;
        var latencies = new List<double>();
        foreach (var seat in seats)
        {
            List<(string Json, long ReceiptStamp)> frames;
            lock (seat.Frames) frames = [.. seat.Frames];
            foreach (var (json, receiptStamp) in frames)
            {
                using var document = JsonDocument.Parse(json);
                var root = document.RootElement;
                if (root.GetProperty("type").GetString() != EdgeTypes.Delta) continue;
                deliveredDeltas++;
                var roomId = root.GetProperty("roomId").GetString()!;
                var tickId = root.GetProperty("tickId").GetInt64();
                if (!publishStamps.TryGetValue((roomId, tickId), out var publishStamp))
                {
                    unkeyed++;
                    continue;
                }
                latencies.Add((receiptStamp - publishStamp) * 1000.0 / Stopwatch.Frequency);
            }
        }

        var dispatched = (long)publishStamps.Count * config.SeatsPerRoom;
        var windowSeconds = Math.Max(0.000001, (endStamp - startStamp) / (double)Stopwatch.Frequency);
        var deliveredCommits = deliveredDeltas / (double)config.SeatsPerRoom;
        long drops = 0;
        foreach (var seat in seats)
        {
            drops += seat.Connection.Outbox.RefusedDeltas
                   + seat.Connection.Outbox.SupersededDeltas
                   + seat.Connection.Outbox.SupersededSnapshots;
        }

        latencies.Sort();
        double P50 = latencies.Count == 0 ? 0 : latencies[latencies.Count / 2];
        double P95 = Percentile(latencies, 0.95);
        double P99 = Percentile(latencies, 0.99);
        var firstError = unkeyed == 0 ? null
            : $"{unkeyed} delivered delta frames had no matching publish stamp";

        return new FrameChannelMeasurement(
            config.Rooms, config.SeatsPerRoom, dispatched, deliveredDeltas,
            deliveredCommits / windowSeconds, P50, P95, P99, drops, firstError);
    }

    private static double Percentile(List<double> sorted, double quantile)
    {
        if (sorted.Count == 0) return 0;
        var index = Math.Min(sorted.Count - 1,
            (int)Math.Ceiling(quantile * sorted.Count) - 1);
        return sorted[Math.Max(0, index)];
    }

    /// <summary>A shared raw-frame counter: the run stops when every seat has heard its
    /// roomJoined plus every delta, which is exactly the honest full-delivery point.</summary>
    private sealed class FrameCounters
    {
        public long Raw;
    }

    /// <summary>One recorded connection: every frame — the direct roomJoined send and the
    /// outbox pump's deltas — lands in one ordered list with the wall-clock receipt stamp,
    /// because both lanes share the serialized transport.</summary>
    private sealed class TimedConnection : IDisposable
    {
        public EdgeConnection Connection { get; }
        public List<(string Json, long ReceiptStamp)> Frames { get; } = [];
        public long LastReceiptStamp;

        public TimedConnection(string id, FrameCounters counts)
        {
            Connection = new EdgeConnection(id, "gw-1", new EdgeOptions());
            Connection.AttachTransport((frame, _) =>
            {
                var stamp = Stopwatch.GetTimestamp();
                lock (Frames) Frames.Add((frame, stamp));
                Volatile.Write(ref LastReceiptStamp, stamp);
                Interlocked.Increment(ref counts.Raw);
                return Task.CompletedTask;
            }, (_, _) => Task.CompletedTask);
            Connection.StartOutboxPump();
        }

        public void Dispose() => Connection.StopOutboxPumpAsync().GetAwaiter().GetResult();
    }

    /// <summary>A scripted subscribe: every call records its request and plays the next
    /// scripted stream (the last script repeats, so a re-subscription is always answered).
    /// Each subscription for the same room is numbered so the producer offsets its ticks
    /// and a re-baseline replay can never collide a latency key.</summary>
    private sealed class ScriptedSync
    {
        public readonly List<RoomStreamRequest> Requests = [];
        private readonly List<Func<RoomStreamRequest, int, CancellationToken, IAsyncEnumerable<SyncMessage>>> _scripts = [];
        private readonly Dictionary<string, int> _subscriptions = new(StringComparer.Ordinal);

        public ScriptedSync Stream(
            Func<RoomStreamRequest, int, CancellationToken, IAsyncEnumerable<SyncMessage>> script)
        {
            _scripts.Add(script);
            return this;
        }

        public IAsyncEnumerable<SyncMessage> Subscribe(RoomStreamRequest request,
            CancellationToken cancellation)
        {
            Requests.Add(request);
            var index = _subscriptions.GetValueOrDefault(request.RoomId);
            _subscriptions[request.RoomId] = index + 1;
            var script = _scripts[Math.Min(_scripts.Count - 1, Requests.Count - 1)];
            return script(request, index, cancellation);
        }
    }

    /// <summary>
    /// The M2 deficit/catch-up frame producer: at most <c>perSecond</c> frames per second,
    /// batched every <c>cadenceMs</c> with a planned-vs-emitted shortfall that catches up a
    /// slow consumer. A bare Task.Delay beat would deliver at a clock cadence no matter how
    /// slow the fanout is; this one yields only when the consumer pulls, so a throttled
    /// fanout surfaces as a delivered-rate shortfall. The stream waits for the room's start
    /// gate (so every seat joins before any frame flows), then parks at the end until the
    /// cancellation a re-subscription or teardown brings.
    /// </summary>
    private static async IAsyncEnumerable<SyncMessage> RateLimitedDeltas(
        string roomId, int subscriptionIndex, double perSecond, int payloadBytes,
        int totalFrames, TaskCompletionSource<bool> gate,
        ConcurrentDictionary<(string RoomId, long TickId), long> publishStamps,
        [EnumeratorCancellation] CancellationToken cancellation)
    {
        const int cadenceMs = 10;
        const int maxBatch = 20;
        var tickBase = subscriptionIndex * 1_000_000L;
        var payload = $"{{\"blob\":\"{new string('a', payloadBytes)}\"}}";
        var start = Stopwatch.GetTimestamp();
        long emitted = 0;

        await gate.Task.WaitAsync(cancellation);
        while (emitted < totalFrames && !cancellation.IsCancellationRequested)
        {
            var before = Stopwatch.GetTimestamp();
            var elapsedMs = (before - start) * 1000.0 / Stopwatch.Frequency;
            var planned = (long)Math.Floor(perSecond * elapsedMs / 1000.0);
            var due = (int)Math.Min(maxBatch, Math.Max(0, planned - emitted));
            for (var i = 0; i < due && emitted < totalFrames; i++)
            {
                emitted++;
                var tick = tickBase + emitted;
                publishStamps[(roomId, tick)] = Stopwatch.GetTimestamp();
                yield return new SyncMessage
                {
                    Delta = new DeltaMessage
                    {
                        RoomId = roomId, Epoch = 1, Tick = tick,
                        StateVersion = emitted, BaseVersion = emitted - 1,
                        ModuleVersion = "mod-1", ConfigHash = "cfg-1",
                        Effects = { new FrameEffectProto { Kind = "score", PayloadJson = payload } }
                    }
                };
            }
            var spent = (Stopwatch.GetTimestamp() - before) * 1000.0 / Stopwatch.Frequency;
            await Task.Delay(Math.Max(0, (int)(cadenceMs - spent)), cancellation);
        }
        await Task.Delay(Timeout.Infinite, cancellation);
    }
}

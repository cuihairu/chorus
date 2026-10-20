using System.Diagnostics;
using System.Text.Json;
using Chorus.Engine.Abstractions;
using Chorus.Engine.Session;
using Chorus.Protocol;
using Chorus.Protocol.Messages;
using Chorus.Protocol.Models;
using Xunit.Abstractions;

namespace Chorus.Engine.Tests;

/// <summary>
/// Opt-in recorder (todo.md:159 触发条款① 单 worker 峰值 CPU &gt; 60%): the tick
/// body's snapshot encode used to visit the game state twice per snapshot -
/// once building a JsonElement DOM, once serializing it to bytes - so the
/// state was serialized twice. The inline write serializes it once. This runs
/// both paths over a state shaped like a 100-player module and reports ns/op
/// plus heap growth per iteration so a run on the new tree is comparable to
/// the same run on the tree before the change (the legacy reference is kept
/// here verbatim).
/// </summary>
/// Recorder only, never a gate: assertions only check that samples were taken,
/// so a slow or loaded machine cannot turn the suite red.
public sealed class WireSnapshotEncodeBenchmarkTests(ITestOutputHelper output)
{
    private const string OptIn = "CHORUS_RUN_SNAPSHOT_ENCODE_BENCH";

    private sealed record Player(long Id, double X, double Y, string Name);

    private sealed record ModuleState(
        long TickId,
        int Frame,
        Player[] Players,
        Dictionary<long, string> LastInput,
        List<string> ReplayLog);

    private static SessionSnapshot Snapshot()
    {
        var players = Enumerable.Range(1, 100)
            .Select(i => new Player(i, i * 0.5, -i * 0.25, $"p{i:D3}-甲{i}"))
            .ToArray();

        return new SessionSnapshot(
            SessionId: "s-encode-bench",
            TickId: 1000,
            ServerTimeMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            State: new ModuleState(1000, 1234, players,
                Enumerable.Range(1, 25).ToDictionary(i => (long)i, i => $"in-{i:D3}"),
                Enumerable.Range(1, 20).Select(i => $"ev-{i:D3}").ToList()),
            Forced: false,
            FrameEvents: Enumerable.Range(1, 4)
                .Select(i => new FrameEvent(1000, $"e-{i}", "input", $"u-{i}")).ToList());
    }

    /// <summary>The pre-optimization encoder: State built as a DOM, then serialized.</summary>
    private static byte[] LegacyEncode(SessionSnapshot snapshot)
    {
        var payload = new ServerSnapshot(
            Type: Chorus.Protocol.ProtocolTypes.Snapshot,
            SessionId: snapshot.SessionId,
            TickId: snapshot.TickId,
            ServerTimeMs: snapshot.ServerTimeMs,
            State: JsonSerializer.SerializeToElement(snapshot.State, ProtocolJson.SerializerOptions),
            Forced: snapshot.Forced,
            Reason: snapshot.Reason,
            TargetClientId: snapshot.TargetClientId,
            ClientLagTicks: snapshot.ClientLagTicks,
            ClientLastAckTickId: snapshot.ClientLastAckTickId,
            V: snapshot.V,
            FrameEvents: snapshot.FrameEvents is { Count: > 0 } ? new List<FrameEvent>(snapshot.FrameEvents) : null);

        return JsonSerializer.SerializeToUtf8Bytes(payload, ProtocolJson.SerializerOptions);
    }

    [Fact]
    [Trait("Category", "Capacity")]
    public void RecordSnapshotEncodeCost_InlineVsLegacyDom()
    {
        if (Environment.GetEnvironmentVariable(OptIn) != "1")
        {
            output.WriteLine($"Snapshot encode benchmark not run; set {OptIn}=1 to opt in.");
            return;
        }

        var runs = int.TryParse(Environment.GetEnvironmentVariable("CHORUS_SNAPSHOT_ENCODE_RUNS"), out var configured)
            ? Math.Clamp(configured, 3, 50) : 5;
        var iterations = int.TryParse(Environment.GetEnvironmentVariable("CHORUS_SNAPSHOT_ENCODE_ITERS"), out var configuredIters)
            ? Math.Clamp(configuredIters, 1000, 200_000) : 20_000;

        var snapshot = Snapshot();
        var payloadBytes = WireSnapshot.Encode(snapshot).EncodedJson.Length;
        output.WriteLine($"state shape=100 players, replayLog=20, lastInput=25 keys, 4 frame events; wire payload={payloadBytes} bytes; runs={runs} iterations={iterations}");

        // Warm the serializer (type reflection, writer pooling) outside the samples.
        WireSnapshot.Encode(snapshot);
        LegacyEncode(snapshot);

        var inline = new List<double>(runs);
        var legacy = new List<double>(runs);
        var inlineAlloc = new List<double>(runs);
        var legacyAlloc = new List<double>(runs);

        foreach (var run in Enumerable.Range(1, runs))
        {
            // Throughput.
            var sw = Stopwatch.StartNew();
            for (var i = 0; i < iterations; i++) WireSnapshot.Encode(snapshot);
            sw.Stop();
            inline.Add(sw.Elapsed.TotalNanoseconds / iterations);

            sw.Restart();
            for (var i = 0; i < iterations; i++) LegacyEncode(snapshot);
            sw.Stop();
            legacy.Add(sw.Elapsed.TotalNanoseconds / iterations);

            // Allocation per encode (the DOM path allocates a whole element tree
            // plus its text buffer on top of the payload bytes).
            byte[] sinkInline = null!, sinkLegacy = null!;
            var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < iterations; i++) sinkInline = WireSnapshot.Encode(snapshot).EncodedJson;
            inlineAlloc.Add((double)(GC.GetAllocatedBytesForCurrentThread() - allocatedBefore) / iterations);

            var allocatedBeforeLegacy = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < iterations; i++) sinkLegacy = LegacyEncode(snapshot);
            legacyAlloc.Add((double)(GC.GetAllocatedBytesForCurrentThread() - allocatedBeforeLegacy) / iterations);

            Assert.NotNull(sinkInline);
            Assert.NotNull(sinkLegacy);
        }

        var inlineStats = Stats(inline);
        var legacyStats = Stats(legacy);
        var inlineAllocStats = Stats(inlineAlloc);
        var legacyAllocStats = Stats(legacyAlloc);

        output.WriteLine($"INLINE   ns/op median={inlineStats.median:F0} min={inlineStats.min:F0} max={inlineStats.max:F0}; alloc bytes/op median={inlineAllocStats.median:F0}");
        output.WriteLine($"LEGACY   ns/op median={legacyStats.median:F0} min={legacyStats.min:F0} max={legacyStats.max:F0}; alloc bytes/op median={legacyAllocStats.median:F0}");
        output.WriteLine($"inline vs legacy: {legacyStats.median / inlineStats.median:0.00}x faster, {legacyAllocStats.median / Math.Max(1.0, inlineAllocStats.median):0.00}x less allocated per encode");
        output.WriteLine("compare against the same run on the tree before the inline write (legacy reference is kept in this test verbatim).");

        Assert.Equal(runs, inline.Count);
        Assert.Equal(runs, legacy.Count);
        Assert.True(inlineStats.min > 0);
        Assert.True(legacyStats.min > 0);
    }

    private static (double median, double min, double max) Stats(IReadOnlyList<double> samples)
    {
        var sorted = samples.OrderBy(x => x).ToList();
        return (sorted[sorted.Count / 2], sorted[0], sorted[^1]);
    }

    /// <summary>
    /// Steady ~6 KiB state shared across ticks, so allocation is attributable
    /// to encoding (not per-tick state construction).
    /// </summary>
    private sealed class FixedStateGameModule : IGameModule
    {
        private readonly object _state;

        public FixedStateGameModule(int players) => _state = new ModuleState(
            1000, 1234,
            Enumerable.Range(1, players).Select(i => new Player(i, i * 0.5, -i * 0.25, $"p{i:D3}-甲{i}")).ToArray(),
            Enumerable.Range(1, 25).ToDictionary(i => (long)i, i => $"in-{i:D3}"),
            Enumerable.Range(1, 20).Select(i => $"ev-{i:D3}").ToList());

        public string Name => "fixed-state-bench";
        public object GetStateSnapshot() => _state;
        public void ApplyEvents(long tickId, IReadOnlyList<AudienceEvent> events) { }
        public void RestoreState(long version, System.Text.Json.JsonElement state) { }
        public void Dispose() { }
    }

    private sealed class DiscardClient : ISessionClient
    {
        public string ClientId { get; }
        public DiscardClient(string clientId) => ClientId = clientId;
        public ValueTask SendSnapshotAsync(SessionSnapshot snapshot, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask SendSnapshotAsync(WireSnapshot wire, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    /// <summary>
    /// A/B recorder for the mixed tick path (periodic snapshot tick while a
    /// lagging client is in the session): reports process-wide allocated bytes
    /// per tick with 3 acking clients + 1 never-acking client. Run the same
    /// recorder on the tree before and after the shared-broadcast encode; the
    /// delta per tick is the serialization saving.
    /// </summary>
    [Fact]
    [Trait("Category", "Capacity")]
    public async Task RecordMixedTickAllocation_ThreeBroadcastOneForced()
    {
        if (Environment.GetEnvironmentVariable(OptIn) != "1")
        {
            output.WriteLine($"Mixed tick benchmark not run; set {OptIn}=1 to opt in.");
            return;
        }

        var seconds = int.TryParse(Environment.GetEnvironmentVariable("CHORUS_MIXED_TICK_SECONDS"), out var configured)
            ? Math.Clamp(configured, 2, 60) : 5;

        await using var actor = new SessionActor("s-mixed-bench", new SessionConfig
        {
            TickDurationMs = 1,
            SnapshotEveryTicks = 1,
            MaxLagTicks = 5,
            // No cooldown: the lagging client is forced on EVERY tick, so the
            // whole window is the mixed path (3 broadcast + 1 forced), not the
            // pure-broadcast fast path.
            ResyncCooldownMs = 0,
        }, new FixedStateGameModule(100));

        for (var i = 0; i < 3; i++)
        {
            await actor.AddClientAsync(new DiscardClient($"ack{i}"));
        }
        await actor.AddClientAsync(new DiscardClient("lag"));
        actor.Start();

        using var stop = new CancellationTokenSource();
        var acker = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                var tick = actor.Diagnostics.TickId;
                for (var i = 0; i < 3; i++)
                {
                    await actor.AckAsync($"ack{i}", tick, 0);
                }
                await Task.Delay(2, stop.Token);
            }
        });

        try
        {
            await Task.Delay(500); // settle: let the lagging client cross MaxLagTicks first
            var startTicks = actor.Diagnostics.TickId;
            var startSent = actor.Diagnostics.TotalSnapshotsSent;
            var startAlloc = GC.GetTotalAllocatedBytes(precise: true);
            await Task.Delay(TimeSpan.FromSeconds(seconds));
            var endAlloc = GC.GetTotalAllocatedBytes(precise: true);
            var endTicks = actor.Diagnostics.TickId;
            var endSent = actor.Diagnostics.TotalSnapshotsSent;

            var ticks = endTicks - startTicks;
            var sent = endSent - startSent;
            var bytes = endAlloc - startAlloc;
            output.WriteLine($"mixed-tick (3 broadcast + 1 forced client): ticks={ticks} snapshotsSent={sent} allocTotal={bytes}B allocPerTick={(double)bytes / Math.Max(1, ticks):F0}B allocPerSnapshot={(double)bytes / Math.Max(1, sent):F0}B");
            output.WriteLine("A/B: run this same recorder on the tree before the shared broadcast encode; the per-tick delta is the saved serialization.");
            Assert.True(ticks > 100, $"too few ticks executed: {ticks}");
            Assert.True(sent > 100, $"too few snapshots sent: {sent}");
        }
        finally
        {
            stop.Cancel();
            try
            {
                await acker;
            }
            catch (OperationCanceledException)
            {
            }
        }
    }
}

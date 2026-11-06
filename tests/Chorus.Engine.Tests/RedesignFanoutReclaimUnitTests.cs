using System.Runtime.CompilerServices;
using System.Text.Json;
using Chorus.Rpc;
using Chorus.StreamerGateway;
using Chorus.StreamerGateway.Messages;
using GrpcChannel = Grpc.Net.Client.GrpcChannel;
using Xunit;

namespace Chorus.Engine.Tests;

/// <summary>
/// The subscription reclamation contract of the room fan-out (architecture 8.3) and the
/// game-host channel pool (FIX-LINK-10): every subscription that opens is released exactly
/// once, the last seat out ends the room's stream, a superseded stream releases only itself,
/// an epoch move follows with a fresh subscription, and a subscription that fails to open
/// leaves the room's live stream serving its seats instead of stranding them on a bumped
/// generation with no reader. The pool's own contract is pinned first: one channel per
/// endpoint, shared by every reference, disposed only at the last reference.
/// </summary>
public sealed class RedesignFanoutReclaimUnitTests
{
    private static SyncMessage Snapshot(long epoch, long tick, long version) => new()
    {
        Snapshot = new SnapshotMessage
        {
            RoomId = "room-1", Epoch = epoch, Tick = tick, StateVersion = version,
            ModuleVersion = "mod-1", ConfigHash = "cfg-1",
            StatePayload = Google.Protobuf.ByteString.CopyFrom([7, 7, 7]),
            StateSchemaVersion = 2
        }
    };

    private static SyncMessage Delta(long epoch, long tick, long version, long baseVersion) => new()
    {
        Delta = new DeltaMessage
        {
            RoomId = "room-1", Epoch = epoch, Tick = tick, StateVersion = version,
            BaseVersion = baseVersion, ModuleVersion = "mod-1", ConfigHash = "cfg-1",
            Effects = { new FrameEffectProto { Kind = "score", PayloadJson = """{"n":3}""" } }
        }
    };

    /// <summary>A live room that has said everything it will say: the stream parks until the
    /// cancellation a re-subscription (or teardown) brings.</summary>
    private static async IAsyncEnumerable<SyncMessage> ParkedAfter(
        SyncMessage message,
        [EnumeratorCancellation] CancellationToken cancellation)
    {
        yield return message;
        await Task.Delay(Timeout.Infinite, cancellation);
    }

    /// <summary>A stream that says one message and then parks until the gate opens — the
    /// stream an epoch move must end, or a failed re-baseline must not tear down.</summary>
    private static async IAsyncEnumerable<SyncMessage> SnapshotThenGate(
        SyncMessage first, TaskCompletionSource gate,
        [EnumeratorCancellation] CancellationToken cancellation)
    {
        yield return first;
        await gate.Task.WaitAsync(cancellation);
    }

    /// <summary>A stream that says one message, waits for the gate, says the next, and ends —
    /// the room's live stream a failed re-baseline must leave serving its seat.</summary>
    private static async IAsyncEnumerable<SyncMessage> SnapshotGateDelta(
        SyncMessage first, SyncMessage second, TaskCompletionSource gate,
        [EnumeratorCancellation] CancellationToken cancellation)
    {
        yield return first;
        await gate.Task.WaitAsync(cancellation);
        yield return second;
    }

    /// <summary>A stream that says two messages, then parks: a seat joining at the head after
    /// the first message rides the second one, and the stream stays live until teardown.</summary>
    private static async IAsyncEnumerable<SyncMessage> SnapshotGateDeltaThenPark(
        SyncMessage first, SyncMessage second, TaskCompletionSource gate,
        [EnumeratorCancellation] CancellationToken cancellation)
    {
        yield return first;
        await gate.Task.WaitAsync(cancellation);
        yield return second;
        await Task.Delay(Timeout.Infinite, cancellation);
    }

    /// <summary>A scripted subscribe: every call records its request and plays the next
    /// scripted stream (the last script repeats, so a re-subscription is always answered).</summary>
    private sealed class ScriptedSync
    {
        public readonly List<RoomStreamRequest> Requests = [];
        private readonly List<Func<CancellationToken, IAsyncEnumerable<SyncMessage>>> _scripts = [];

        public ScriptedSync Stream(Func<CancellationToken, IAsyncEnumerable<SyncMessage>> script)
        {
            _scripts.Add(script);
            return this;
        }

        public IAsyncEnumerable<SyncMessage> Subscribe(RoomStreamRequest request,
            CancellationToken cancellation)
        {
            Requests.Add(request);
            var script = _scripts[Math.Min(_scripts.Count - 1, Requests.Count - 1)];
            return script(cancellation);
        }
    }

    /// <summary>One recorded connection: every frame — direct or through the outbox pump —
    /// lands in one ordered list, because both lanes share the serialized transport.</summary>
    private sealed class RecordedConnection : IDisposable
    {
        public EdgeConnection Connection { get; }
        public List<string> Frames { get; } = [];

        public RecordedConnection(string id)
        {
            Connection = new EdgeConnection(id, "gw-1", new EdgeOptions());
            Connection.AttachTransport((frame, _) =>
            {
                lock (Frames) Frames.Add(frame);
                return Task.CompletedTask;
            }, (_, _) => Task.CompletedTask);
            Connection.StartOutboxPump();
        }

        public void Dispose() => Connection.StopOutboxPumpAsync().GetAwaiter().GetResult();
    }

    private static EdgeRoomFanout Fanout(ScriptedSync sync, Action<string>? onClosed,
        Func<Task<EdgeRoomFanout.ResolvedRoom?>>? resolve = null)
        => new(sync.Subscribe)
        {
            ResolveRoom = (_, _) => resolve is null
                ? Task.FromResult<EdgeRoomFanout.ResolvedRoom?>(null)
                : resolve(),
            StreamClosed = onClosed
        };

    private static async Task<List<JsonElement>> WaitAsync(RecordedConnection connection,
        int frameCount)
    {
        var deadline = Environment.TickCount64 + 15_000;
        while (Environment.TickCount64 < deadline)
        {
            List<string> snapshot;
            lock (connection.Frames) snapshot = [.. connection.Frames];
            if (snapshot.Count >= frameCount)
                return snapshot.Select(f => JsonDocument.Parse(f).RootElement).ToList();
            await Task.Delay(25, CancellationToken.None);
        }
        lock (connection.Frames)
            throw new TimeoutException(
                $"Expected {frameCount} frames, saw {connection.Frames.Count}: "
                + string.Join("|", connection.Frames));
    }

    private static async Task WaitForAsync(Func<bool> condition, string what)
    {
        var deadline = Environment.TickCount64 + 15_000;
        while (Environment.TickCount64 < deadline)
        {
            if (condition()) return;
            await Task.Delay(25, CancellationToken.None);
        }
        throw new TimeoutException($"Timed out waiting for: {what}");
    }

    [Fact]
    public void ThePoolDisposesAChannelOnlyWhenItsLastReferenceDrops()
    {
        var created = new List<string>();
        var pool = new EdgeGameChannelPool(endpoint =>
        {
            created.Add(endpoint);
            return GrpcChannel.ForAddress("https://localhost:" + (1000 + created.Count));
        });
        var first = pool.Acquire("ep-a");
        Assert.Same(first, pool.Acquire("ep-a"));
        Assert.Single(created);
        // The first release still holds the channel (a second reference is out there).
        pool.Release("ep-a");
        Assert.Same(first, pool.Acquire("ep-a"));
        // The last reference drops: the channel is released and a later acquisition is fresh.
        pool.Release("ep-a");
        pool.Release("ep-a");
        var second = pool.Acquire("ep-a");
        Assert.NotSame(first, second);
        Assert.Equal(2, created.Count);
        pool.Release("ep-a");
        // Distinct endpoints own distinct channels.
        var other = pool.Acquire("ep-b");
        Assert.NotSame(second, other);
        Assert.Equal(3, created.Count);
        pool.Release("ep-b");
        // A release beyond the last reference is a no-op: it neither throws nor disturbs a
        // later acquisition (a double-firing reader cannot dispose a channel in use).
        pool.Release("ep-a");
        Assert.Same(pool.Acquire("ep-a"), pool.Acquire("ep-a"));
    }

    [Fact]
    public async Task TheLastSeatOutEndsTheRoomsStreamAndReleasesItOnce()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sync = new ScriptedSync()
            .Stream(ct => SnapshotGateDeltaThenPark(
                Snapshot(4, 1, 1), Delta(4, 2, 2, 1), gate, ct));
        var releases = 0;
        var fanout = Fanout(sync, _ => Interlocked.Increment(ref releases));
        using var a = new RecordedConnection("conn-a");
        using var b = new RecordedConnection("conn-b");
        using var c = new RecordedConnection("conn-c");
        fanout.Join("room-1", "game-1", "ep-a", 4, "streamer", a.Connection);
        await WaitAsync(a, 1);
        // The second seat joins at the stream head: one shared subscription for both seats.
        b.Connection.MarkApplied("room-1", 1, 1);
        fanout.Join("room-1", "game-1", "ep-a", 4, "challenger", b.Connection);
        Assert.Single(sync.Requests);
        Assert.Equal(0, releases);
        // The delta arrives for both seats on the one shared subscription — conn-b, at the
        // head, rides deltas alone (no re-baseline snapshot, no second subscription).
        gate.SetResult();
        await WaitAsync(a, 3);   // roomJoined, snapshot, delta
        await WaitAsync(b, 2);   // roomJoined, delta
        Assert.Single(sync.Requests);
        Assert.Equal(0, releases);
        // The first seat leaves: the shared subscription must survive the departure.
        fanout.Leave("conn-a");
        await Task.Delay(150);
        Assert.Single(sync.Requests);
        Assert.Equal(0, releases);
        // The last seat leaves: the room's stream ends and its subscription releases once.
        fanout.Leave("conn-b");
        await WaitForAsync(() => releases == 1, "the last seat out releases the subscription once");
        Assert.Equal(1, releases);
        // A fresh join starts a brand-new subscription — the room was removed, not parked.
        fanout.Join("room-1", "game-1", "ep-a", 4, "streamer", c.Connection);
        await WaitAsync(c, 1);
        Assert.Equal(2, sync.Requests.Count);
    }

    [Fact]
    public async Task ASupersededBaselineReleasesTheOldStreamOnce()
    {
        var sync = new ScriptedSync().Stream(ct => ParkedAfter(Snapshot(4, 1, 1), ct));
        var releases = 0;
        var fanout = Fanout(sync, _ => Interlocked.Increment(ref releases));
        using var a = new RecordedConnection("conn-a");
        fanout.Join("room-1", "game-1", "ep-a", 4, "streamer", a.Connection);
        await WaitAsync(a, 1);
        Assert.Equal(1, sync.Requests.Count);
        Assert.Equal(0, releases);
        // A re-baseline supersedes the running stream: the old subscription releases once
        // and the replacement keeps the room served (no second release while it runs).
        Assert.True(fanout.RequestSnapshot("conn-a", "room-1"));
        await WaitForAsync(() => releases == 1, "the superseded stream releases once");
        Assert.Equal(2, sync.Requests.Count);
        Assert.Equal(0, sync.Requests[1].LastAppliedTick);
        await Task.Delay(150);
        Assert.Equal(1, releases);
        // The replacement ends with the room: total releases match total subscriptions.
        fanout.Leave("conn-a");
        await WaitForAsync(() => releases == 2, "the replacement releases on the last seat out");
        Assert.Equal(2, sync.Requests.Count);
    }

    [Fact]
    public async Task AnEpochMoveReleasesTheOldStreamAndSubscribesFresh()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sync = new ScriptedSync()
            // The first stream: a snapshot, then it parks until the room's epoch moves.
            .Stream(ct => SnapshotThenGate(Snapshot(4, 1, 1), release, ct))
            // The epoch-moved replacement: a live room at the new epoch.
            .Stream(ct => ParkedAfter(Snapshot(5, 1, 1), ct));
        var releases = 0;
        var fanout = Fanout(sync, _ => Interlocked.Increment(ref releases),
            () => Task.FromResult<EdgeRoomFanout.ResolvedRoom?>(
                new("game-1", "ep-a", 5, "Live")));
        using var a = new RecordedConnection("conn-a");
        fanout.Join("room-1", "game-1", "ep-a", 4, "streamer", a.Connection);
        await WaitAsync(a, 1);
        Assert.Equal(1, sync.Requests.Count);
        // The stream ends: the room moved to a new epoch, so the room re-subscribes there
        // and the ended subscription is released once — not on every resolve.
        release.SetResult();
        await WaitForAsync(() => sync.Requests.Count == 2, "the moved room re-subscribes");
        await WaitForAsync(() => releases == 1, "the moved-away stream releases once");
        Assert.Equal(5, sync.Requests[1].Epoch);
        await WaitAsync(a, 2);
        await Task.Delay(150);
        Assert.Equal(1, releases);
        // The replacement ends with the room: total releases match total subscriptions.
        fanout.Leave("conn-a");
        await WaitForAsync(() => releases == 2, "the replacement releases on the last seat out");
    }

    [Fact]
    public async Task AFailedResubscriptionLeavesTheExistingStreamServingTheRoom()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sync = new ScriptedSync()
            // The room's live stream: a snapshot, a delta the moment the gate opens, then end.
            .Stream(ct => SnapshotGateDelta(Snapshot(4, 1, 1), Delta(4, 2, 2, 1), release, ct))
            // A second seat's re-baseline subscribe fails synchronously (a channel the
            // factory cannot build) — the room must not lose its working stream over it.
            .Stream(_ => throw new InvalidOperationException("subscribe failed"));
        var releases = 0;
        var fanout = Fanout(sync, _ => Interlocked.Increment(ref releases));
        using var a = new RecordedConnection("conn-a");
        using var b = new RecordedConnection("conn-b");
        fanout.Join("room-1", "game-1", "ep-a", 4, "streamer", a.Connection);
        await WaitAsync(a, 1);
        Assert.Equal(1, sync.Requests.Count);
        Assert.Equal(0, releases);
        // The failed subscribe releases its own attempt once and rethrows; the seat is
        // rolled back and the room keeps its live stream serving the first seat.
        Assert.Throws<InvalidOperationException>(() =>
            fanout.Join("room-1", "game-1", "ep-a", 4, "challenger", b.Connection));
        Assert.Equal(2, sync.Requests.Count);
        Assert.Equal(1, releases);
        // The live stream was not torn down by the failed re-baseline: it still delivers.
        release.SetResult();
        var frames = await WaitAsync(a, 3);   // roomJoined, snapshot, delta
        Assert.Equal("delta", frames[2].GetProperty("type").GetString());
        Assert.Equal(2, frames[2].GetProperty("stateVersion").GetInt64());
        // The stream ends naturally: the room resolves and releases once more — total
        // releases match total subscriptions (one success, one failed attempt).
        await WaitForAsync(() => releases == 2, "the live stream releases when it ends");
        Assert.Equal(2, sync.Requests.Count);
    }
}

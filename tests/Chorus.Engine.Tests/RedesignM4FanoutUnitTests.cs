using System.Runtime.CompilerServices;
using System.Text.Json;
using Chorus.Redesign.Rpc;
using Chorus.Redesign.StreamerGateway;
using Chorus.Redesign.StreamerGateway.Messages;
using Xunit;

namespace Chorus.Engine.Tests;

/// <summary>
/// The room fan-out of M4-4 without a control plane: a scripted sync stream drives the
/// state machine through the properties the contract pins — the chain a seat sees
/// (roomJoined before any frame, deltas that chain, the sticky result), the re-baseline a
/// late seat forces on the whole room, the re-subscription a moved epoch causes, and the
/// honest notices (roomSuspended, resyncRequired) a stream that ends badly produces.
/// </summary>
public sealed class RedesignM4FanoutUnitTests
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

    private static SyncMessage Result(long epoch, long tick, long version) => new()
    {
        RoundResult = new RoundResultMessage
        {
            RoomId = "room-1", Epoch = epoch, Tick = tick, StateVersion = version,
            ResultJson = """{"ranking":[{"participant":"a","score":9}]}"""
        }
    };

    private static async IAsyncEnumerable<SyncMessage> Finite(params SyncMessage[] messages)
    {
        foreach (var message in messages)
            yield return message;
    }

    /// <summary>A live room that has said everything it will say: the stream parks until the
    /// cancellation a re-subscription (or teardown) brings.</summary>
    private static async IAsyncEnumerable<SyncMessage> ParkedAfter(
        SyncMessage message,
        [EnumeratorCancellation] CancellationToken cancellation)
    {
        yield return message;
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

    private static EdgeRoomFanout Fanout(ScriptedSync sync,
        Func<Task<EdgeRoomFanout.ResolvedRoom?>>? resolve = null)
        => new(sync.Subscribe)
        {
            ResolveRoom = (_, _) => resolve is null
                ? Task.FromResult<EdgeRoomFanout.ResolvedRoom?>(null)
                : resolve()
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

    [Fact]
    public async Task ASeatSeesRoomJoinedThenAChainedSnapshotDeltaResultSequence()
    {
        var sync = new ScriptedSync()
            .Stream(ct => Finite(
                Snapshot(4, 0, 1), Delta(4, 1, 2, 1), Result(4, 2, 3)));
        var fanout = Fanout(sync);
        using var seat = new RecordedConnection("conn-a");
        fanout.Join("room-1", "game-a", "https://host-1", 4, "streamer", seat.Connection);

        var frames = await WaitAsync(seat, 4);
        Assert.Equal("roomJoined", frames[0].GetProperty("type").GetString());
        Assert.Equal("room-1", frames[0].GetProperty("roomId").GetString());
        Assert.Equal(4, frames[0].GetProperty("roomEpoch").GetInt64());
        Assert.Equal("streamer", frames[0].GetProperty("participation").GetString());
        Assert.Equal("mod-1", frames[0].GetProperty("moduleVersion").GetString());
        Assert.Equal("cfg-1", frames[0].GetProperty("configHash").GetString());

        Assert.Equal("snapshot", frames[1].GetProperty("type").GetString());
        Assert.Equal(1, frames[1].GetProperty("stateVersion").GetInt64());
        Assert.Equal(2, frames[1].GetProperty("schemaVersion").GetInt32());
        // The state payload rides as base64, like every byte field on the wire.
        Assert.Equal("BwcH", frames[1].GetProperty("payload").GetString());

        Assert.Equal("delta", frames[2].GetProperty("type").GetString());
        Assert.Equal(1, frames[2].GetProperty("baseVersion").GetInt64());
        Assert.Equal(2, frames[2].GetProperty("stateVersion").GetInt64());
        // The payload is the frame's effects as a JSON array — the module's own vocabulary.
        Assert.Equal("""[{"kind":"score","payload":{"n":3}}]""",
            frames[2].GetProperty("payload").GetRawText());

        Assert.Equal("roundResult", frames[3].GetProperty("type").GetString());
        Assert.Equal("room-1:4:2", frames[3].GetProperty("resultId").GetString());
        Assert.Equal("Committed", frames[3].GetProperty("settlementState").GetString());
        Assert.Equal(1, frames[3].GetProperty("result").GetProperty("ranking").GetArrayLength());

        // The stream completed after the result: the room ends quietly — no resyncRequired —
        // and a later requestSnapshot has no room to re-baseline.
        await Task.Delay(200, CancellationToken.None);
        lock (seat.Frames)
            Assert.DoesNotContain(seat.Frames, frame => frame.Contains("resyncRequired"));
        Assert.False(fanout.RequestSnapshot("conn-a", "room-1"));
        // One subscription served the whole chain.
        Assert.Single(sync.Requests);
    }

    [Fact]
    public async Task ALateSeatRebaselinesTheRoomAtItsOwnBaseline()
    {
        var sync = new ScriptedSync()
            .Stream(ct => ParkedAfterChain(ct, Snapshot(4, 0, 1), Delta(4, 1, 2, 1)))
            .Stream(ct => ParkedAfter(Snapshot(4, 1, 2), ct));
        var fanout = Fanout(sync);
        using var seatA = new RecordedConnection("conn-a");
        using var seatB = new RecordedConnection("conn-b");
        fanout.Join("room-1", "game-a", "https://host-1", 4, "streamer", seatA.Connection);
        await WaitAsync(seatA, 3);

        // A late seat at (0,0) cannot ride a stream whose head is (1,2): the room
        // re-subscribes at the late seat's baseline.
        fanout.Join("room-1", "game-a", "https://host-1", 4, "challenger", seatB.Connection);
        var framesB = await WaitAsync(seatB, 2);
        Assert.Equal("roomJoined", framesB[0].GetProperty("type").GetString());
        Assert.Equal("challenger", framesB[0].GetProperty("participation").GetString());
        Assert.Equal("snapshot", framesB[1].GetProperty("type").GetString());

        Assert.Equal(2, sync.Requests.Count);
        Assert.Equal(0, sync.Requests[1].LastAppliedTick);
        Assert.Equal(0, sync.Requests[1].LastAppliedStateVersion);
        // The seat that was already at the head is re-baselined too — a later snapshot, not a
        // gap it has to guess across.
        var framesA = await WaitAsync(seatA, 4);
        Assert.Equal("snapshot", framesA[3].GetProperty("type").GetString());
        Assert.Equal(2, framesA[3].GetProperty("stateVersion").GetInt64());
    }

    private static async IAsyncEnumerable<SyncMessage> ParkedAfterChain(
        [EnumeratorCancellation] CancellationToken cancellation,
        params SyncMessage[] messages)
    {
        foreach (var message in messages)
            yield return message;
        await Task.Delay(Timeout.Infinite, cancellation);
    }

    [Fact]
    public async Task AStreamThatEndsAtANewEpochResubscribesAndReAnnouncesTheRoom()
    {
        var sync = new ScriptedSync()
            .Stream(ct => Finite())                                   // epoch 4 ends bare
            .Stream(ct => ParkedAfter(Snapshot(5, 0, 1), ct));        // the moved room answers
        // The resolver reports the room the routing world sees now: epoch 5.
        var fanout = Fanout(sync, () =>
            Task.FromResult<EdgeRoomFanout.ResolvedRoom?>(
                new EdgeRoomFanout.ResolvedRoom("game-a", "https://host-1", 5, "Running")));
        using var seat = new RecordedConnection("conn-a");
        fanout.Join("room-1", "game-a", "https://host-1", 4, "streamer", seat.Connection);

        // The bare stream resolves at a newer epoch: the fan-out follows the room.
        var frames = await WaitAsync(seat, 2);
        Assert.Equal(2, sync.Requests.Count);
        Assert.Equal(5, sync.Requests[1].Epoch);
        Assert.Equal(0, sync.Requests[1].LastAppliedTick);
        // The re-subscribed snapshot re-announces the room at the epoch it now carries.
        Assert.Equal("roomJoined", frames[0].GetProperty("type").GetString());
        Assert.Equal(5, frames[0].GetProperty("roomEpoch").GetInt64());
        Assert.Equal("snapshot", frames[1].GetProperty("type").GetString());
        Assert.Equal(5, frames[1].GetProperty("roomEpoch").GetInt64());
    }

    [Fact]
    public async Task AConsumedCommandReceiptFollowsItsDeltaToTheSeatThatSentIt()
    {
        // The delta waits on a signal: the command must be tracked before the frame that
        // consumes it exists, exactly like the wire (track, store, then the tick commits).
        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sync = new ScriptedSync().Stream(ct => DeltaAfterSignal(signal, ct));
        var fanout = Fanout(sync);
        using var seat = new RecordedConnection("conn-a");
        fanout.Join("room-1", "game-a", "https://host-1", 4, "streamer", seat.Connection);
        await WaitAsync(seat, 2);
        Assert.True(fanout.TrackCommand("room-1", "cmd-1", "conn-a"));
        // A connection that holds no seat, and a room that does not exist, track nothing —
        // the receipt's routing is best-effort, never the command's fate.
        Assert.False(fanout.TrackCommand("room-1", "cmd-other", "conn-gone"));
        Assert.False(fanout.TrackCommand("room-none", "cmd-2", "conn-a"));

        signal.SetResult();
        // delta then its receipt — in that order, on the ordered lane.
        var frames = await WaitAsync(seat, 4);
        Assert.Equal("delta", frames[2].GetProperty("type").GetString());
        Assert.Equal("commandReceipt", frames[3].GetProperty("type").GetString());
        Assert.Equal("cmd-1", frames[3].GetProperty("commandId").GetString());
        Assert.Equal("Applied", frames[3].GetProperty("status").GetString());
        Assert.Equal("room-1:7", frames[3].GetProperty("receiptId").GetString());
        // The untracked command in the same delta routes nowhere.
        await Task.Delay(200, CancellationToken.None);
        lock (seat.Frames) Assert.Equal(4, seat.Frames.Count);
    }

    private static async IAsyncEnumerable<SyncMessage> DeltaAfterSignal(
        TaskCompletionSource signal, [EnumeratorCancellation] CancellationToken cancellation)
    {
        yield return Snapshot(4, 0, 1);
        await signal.Task.WaitAsync(cancellation);
        yield return new SyncMessage
        {
            Delta = new DeltaMessage
            {
                RoomId = "room-1", Epoch = 4, Tick = 1, StateVersion = 2,
                BaseVersion = 1, ModuleVersion = "mod-1", ConfigHash = "cfg-1",
                Commands =
                {
                    new CommandReceiptProto { CommandId = "cmd-1", Sequence = 7 },
                    new CommandReceiptProto { CommandId = "cmd-other", Sequence = 8 }
                }
            }
        };
        await Task.Delay(Timeout.Infinite, cancellation);
    }

    [Fact]
    public void CommandAndLeaveUplinksSpeakTheContractFieldNames()
    {
        var command = JsonSerializer.Serialize(new EdgeGameCommand
        {
            CommandId = "cmd-1", RoomId = "room-1", RoomEpoch = 4, ClientSequence = 2,
            Payload = JsonDocument.Parse("""{"action":"boost"}""").RootElement
        }, EdgeJson.Options);
        Assert.Contains("\"type\":\"gameCommand\"", command);
        Assert.Contains("\"commandId\":\"cmd-1\"", command);
        Assert.Contains("\"roomId\":\"room-1\"", command);
        Assert.Contains("\"roomEpoch\":4", command);
        Assert.Contains("\"clientSequence\":2", command);
        Assert.Contains("\"payload\":{\"action\":\"boost\"}", command);

        var leave = JsonSerializer.Serialize(new EdgeLeaveRound
        {
            OperationId = "leave-1", RoomId = "room-1", Reason = "done"
        }, EdgeJson.Options);
        Assert.Contains("\"type\":\"leaveRound\"", leave);
        Assert.Contains("\"operationId\":\"leave-1\"", leave);
        Assert.Contains("\"reason\":\"done\"", leave);

        var receipt = JsonSerializer.Serialize(new EdgeCommandReceipt
        {
            CommandId = "cmd-1", Status = "Accepted", ReceiptId = "room-1:7"
        }, EdgeJson.Options);
        Assert.Contains("\"type\":\"commandReceipt\"", receipt);
        Assert.Contains("\"status\":\"Accepted\"", receipt);
        Assert.Contains("\"receiptId\":\"room-1:7\"", receipt);
    }

    [Fact]
    public async Task AParkedRoomSurfacesAsSuspendedAndAVanishedRoomAsResync()
    {
        // Suspended: the client learns the room parked itself — visibly, with the recovery
        // epoch every recovery lands on — and the room is gone.
        var sync = new ScriptedSync().Stream(ct => Finite());
        var fanout = Fanout(sync, () =>
            Task.FromResult<EdgeRoomFanout.ResolvedRoom?>(
                new EdgeRoomFanout.ResolvedRoom("game-a", "https://host-1", 6, "Suspended")));
        using var seat = new RecordedConnection("conn-a");
        fanout.Join("room-1", "game-a", "https://host-1", 6, "streamer", seat.Connection);
        var frames = await WaitAsync(seat, 1);
        Assert.Equal("roomSuspended", frames[0].GetProperty("type").GetString());
        Assert.Equal(6, frames[0].GetProperty("recoveryAttempt").GetInt64());
        Assert.False(fanout.RequestSnapshot("conn-a", "room-1"));

        // Vanished: no resolvable room at all is the honest resync, never a freeze.
        var vanished = Fanout(new ScriptedSync().Stream(ct => Finite()),
            () => Task.FromResult<EdgeRoomFanout.ResolvedRoom?>(null));
        using var seatB = new RecordedConnection("conn-b");
        vanished.Join("room-2", "game-a", "https://host-1", 6, "streamer", seatB.Connection);
        var framesB = await WaitAsync(seatB, 1);
        Assert.Equal("resyncRequired", framesB[0].GetProperty("type").GetString());
        Assert.Equal("room-2", framesB[0].GetProperty("roomId").GetString());
        Assert.Equal(6, framesB[0].GetProperty("roomEpoch").GetInt64());
    }
}

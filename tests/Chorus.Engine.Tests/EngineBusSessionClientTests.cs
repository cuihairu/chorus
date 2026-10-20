using System.Text.Json;
using Chorus.Engine.Session;
using Chorus.EngineHost;
using Chorus.Messaging;
using Chorus.Messaging.Ipc;
using Chorus.Protocol;
using Chorus.Protocol.Messages;

namespace Chorus.Engine.Tests;

/// <summary>
/// Publish dedup on the engine side of the IPC (shared-memory-transport.md
/// alt 1): a broadcast snapshot (no target client) is identical for every
/// client and the gateway already fans one null-target snapshot out to the
/// whole session, so only the session's broadcast owner publishes it; a
/// directed resync snapshot publishes from the targeted client alone. Same
/// WS behavior on the far side, N× fewer eng→gw messages per tick.
/// </summary>
public sealed class EngineBusSessionClientTests
{
    private sealed class RecordingBus : IMessageBus
    {
        public List<Envelope> Published { get; } = new();

        public ValueTask PublishAsync(Envelope envelope, CancellationToken cancellationToken)
        {
            Published.Add(envelope);
            return ValueTask.CompletedTask;
        }

        public async IAsyncEnumerable<Envelope> SubscribeAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FailingBus : IMessageBus
    {
        public int Attempts { get; private set; }

        public ValueTask PublishAsync(Envelope envelope, CancellationToken cancellationToken)
        {
            Attempts++;
            throw new InvalidOperationException("bus down");
        }

        public async IAsyncEnumerable<Envelope> SubscribeAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static SessionSnapshot BroadcastSnapshot() => new(
        SessionId: "s-bus",
        TickId: 7,
        ServerTimeMs: 1_000,
        State: new { applied = 1 },
        Forced: false);

    private static SessionSnapshot DirectedSnapshot(string clientId) => new(
        SessionId: "s-bus",
        TickId: 7,
        ServerTimeMs: 1_000,
        State: new { applied = 1 },
        Forced: true,
        Reason: "lag",
        TargetClientId: clientId,
        ClientLagTicks: 3,
        ClientLastAckTickId: 4);

    [Fact]
    public async Task Broadcast_PublishedOnce_ByOwnerOnly()
    {
        var bus = new RecordingBus();
        var owner = new EngineBusSessionClient(bus, "s-bus", "c1", isBroadcastOwner: () => true, onPublishFailure: (_, _) => { });
        var follower = new EngineBusSessionClient(bus, "s-bus", "c2", isBroadcastOwner: () => false, onPublishFailure: (_, _) => { });

        await owner.SendSnapshotAsync(BroadcastSnapshot(), CancellationToken.None);
        await follower.SendSnapshotAsync(BroadcastSnapshot(), CancellationToken.None);

        var env = Assert.Single(bus.Published);
        Assert.Equal(IpcMessageTypes.EngineSnapshot, env.Type);
        Assert.Equal("s-bus", env.SessionId);
    }

    [Fact]
    public async Task Directed_PublishedOnlyByTargetClient()
    {
        var bus = new RecordingBus();
        var c1 = new EngineBusSessionClient(bus, "s-bus", "c1", isBroadcastOwner: () => true, onPublishFailure: (_, _) => { });
        var c2 = new EngineBusSessionClient(bus, "s-bus", "c2", isBroadcastOwner: () => false, onPublishFailure: (_, _) => { });

        await c1.SendSnapshotAsync(DirectedSnapshot("c2"), CancellationToken.None);
        await c2.SendSnapshotAsync(DirectedSnapshot("c2"), CancellationToken.None);

        var env = Assert.Single(bus.Published);
        var body = EnvelopeFactory.DeserializeBody<ServerSnapshot>(env);
        Assert.Equal("c2", body.TargetClientId);
    }

    [Fact]
    public async Task Snapshot_StateJson_RidesTheBusUnchanged()
    {
        // The bus path used to materialize the state as a JsonElement DOM before
        // publishing, so every snapshot visited the state twice. The envelope
        // body must carry the same state JSON either way.
        var bus = new RecordingBus();
        var client = new EngineBusSessionClient(bus, "s-bus", "c1", isBroadcastOwner: () => true, onPublishFailure: (_, _) => { });
        var snapshot = BroadcastSnapshot() with
        {
            State = new { players = new[] { new { id = 7, x = 1.5, name = "甲" } } }
        };

        await client.SendSnapshotAsync(snapshot, CancellationToken.None);

        var body = EnvelopeFactory.DeserializeBody<ServerSnapshot>(Assert.Single(bus.Published));
        var state = Assert.IsType<JsonElement>(body.State);
        Assert.Equal(
            JsonSerializer.Serialize(snapshot.State, ProtocolJson.SerializerOptions),
            state.GetRawText());
    }

    [Fact]
    public async Task NoOwner_BroadcastSkipped()
    {
        // Transfer window between an owner disconnect and the next tick:
        // one broadcast may be skipped rather than duplicated.
        var bus = new RecordingBus();
        var c1 = new EngineBusSessionClient(bus, "s-bus", "c1", isBroadcastOwner: () => false, onPublishFailure: (_, _) => { });

        await c1.SendSnapshotAsync(BroadcastSnapshot(), CancellationToken.None);

        Assert.Empty(bus.Published);
    }

    [Fact]
    public async Task Snapshot_CarriesSchemaVersion_OverIpc()
    {
        // versioning.md §2: the snapshot schema version (`v`) rides the
        // eng→gw IPC payload too, so the gateway relays it to WS clients
        // unchanged. Assert pass-through with a non-default version so a
        // hardcoded 1 cannot satisfy the test.
        var bus = new RecordingBus();
        var client = new EngineBusSessionClient(bus, "s-bus", "c1", isBroadcastOwner: () => true, onPublishFailure: (_, _) => { });

        await client.SendSnapshotAsync(BroadcastSnapshot() with { V = 2 }, CancellationToken.None);

        var body = EnvelopeFactory.DeserializeBody<ServerSnapshot>(Assert.Single(bus.Published));
        Assert.Equal(2, body.V);
    }

    [Fact]
    public async Task PublishFailure_ReportedAndSwallowed()
    {
        // observability.md: a snapshot publish failure at the engine boundary
        // is best-effort (the tick must not die) but must not vanish — it is
        // reported so the service can count and expose it.
        var bus = new FailingBus();
        var failures = new List<(string Type, string Message)>();
        var client = new EngineBusSessionClient(
            bus, "s-bus", "c1",
            isBroadcastOwner: () => true,
            onPublishFailure: (env, _) => failures.Add((env.Type, env.MessageId)));

        // Must not throw: best-effort semantics preserved.
        await client.SendSnapshotAsync(BroadcastSnapshot(), CancellationToken.None);

        Assert.Equal(1, bus.Attempts);
        var failure = Assert.Single(failures);
        Assert.Equal(IpcMessageTypes.EngineSnapshot, failure.Type);
    }
}

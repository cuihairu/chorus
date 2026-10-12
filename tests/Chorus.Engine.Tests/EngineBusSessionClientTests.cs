using Chorus.Engine.Session;
using Chorus.EngineHost;
using Chorus.Messaging;
using Chorus.Messaging.Ipc;
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
        var owner = new EngineBusSessionClient(bus, "s-bus", "c1", isBroadcastOwner: () => true);
        var follower = new EngineBusSessionClient(bus, "s-bus", "c2", isBroadcastOwner: () => false);

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
        var c1 = new EngineBusSessionClient(bus, "s-bus", "c1", isBroadcastOwner: () => true);
        var c2 = new EngineBusSessionClient(bus, "s-bus", "c2", isBroadcastOwner: () => false);

        await c1.SendSnapshotAsync(DirectedSnapshot("c2"), CancellationToken.None);
        await c2.SendSnapshotAsync(DirectedSnapshot("c2"), CancellationToken.None);

        var env = Assert.Single(bus.Published);
        var body = EnvelopeFactory.DeserializeBody<ServerSnapshot>(env);
        Assert.Equal("c2", body.TargetClientId);
    }

    [Fact]
    public async Task NoOwner_BroadcastSkipped()
    {
        // Transfer window between an owner disconnect and the next tick:
        // one broadcast may be skipped rather than duplicated.
        var bus = new RecordingBus();
        var c1 = new EngineBusSessionClient(bus, "s-bus", "c1", isBroadcastOwner: () => false);

        await c1.SendSnapshotAsync(BroadcastSnapshot(), CancellationToken.None);

        Assert.Empty(bus.Published);
    }
}

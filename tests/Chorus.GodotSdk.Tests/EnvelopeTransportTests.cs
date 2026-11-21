using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Chorus.GodotSdk;
using Xunit;

namespace Chorus.GodotSdk.Tests;

/// <summary>
/// The transport binding (EK-1): the <see cref="EnvelopeTransport"/> maps the SDK's
/// typed intents and answers onto Envelope bytes on a channel, and the bytes are the
/// codec's — the loopback channel here encodes/decodes through
/// <see cref="GodotEnvelopeCodec"/>, exactly like a socket would, so these tests prove
/// the codec is the framing a transport actually speaks, not a library beside one.
/// </summary>
public sealed class EnvelopeTransportTests
{
    [Fact]
    public async Task LoginRidesTheEnvelopeWireAndAnswersTheTypedFacts()
    {
        var channel = new LoopbackChannel();
        var transport = new EnvelopeTransport(channel);

        var loginTask = transport.LoginAsync(new LoginIntent
        {
            Platform = "douyin",
            AppId = "app-1",
            LaunchCredential = "{\"launch\":\"cred\"}",
            LoginAttemptId = "attempt-1",
            NextResumeSecret = new byte[32],
        }, default);

        // The wire carried the request: an sdk.login envelope whose body is the intent.
        var sent = await channel.TakeSentAsync();
        Assert.Equal("sdk.login", sent.Type);
        Assert.Equal("douyin", sent.Body.GetProperty("platform").GetString());

        // The peer answers with an sdk.login envelope carrying the answer body.
        channel.Reply("sdk.login", new LoginAnswer
        {
            SessionId = "sess-9",
            AccessCredential = "cred-9",
            ResumeRegistered = true,
        });
        var answer = await loginTask;

        Assert.Equal("sess-9", answer.SessionId);
        Assert.Equal("cred-9", answer.AccessCredential);
        Assert.True(answer.ResumeRegistered);
    }

    [Fact]
    public async Task ARefusedClosedWireIsVisible()
    {
        var channel = new LoopbackChannel();
        var transport = new EnvelopeTransport(channel);
        await transport.CloseAsync();

        await Assert.ThrowsAsync<GodotTransportException>(() =>
            transport.LoginAsync(new LoginIntent(), default));
    }

    [Fact]
    public async Task TheSyncStreamPullsFramesAndEndsOnTheParkNotice()
    {
        var channel = new LoopbackChannel();
        var transport = new EnvelopeTransport(channel);

        var subscribeTask = transport.SubscribeAsync("room-1", 3, -1, -1, default);
        var sent = await channel.TakeSentAsync();
        Assert.Equal("room-1", sent.Body.GetProperty("roomId").GetString());
        channel.Reply("sdk.sync", new { }); // the subscribe's own ack

        var stream = await subscribeTask;

        // A snapshot frame, then the park notice: the notice is the subscription's last
        // frame and the next advance answers false.
        channel.Reply("sdk.sync", SyncBody("snapshot", new SnapshotFrame
        {
            RoomId = "room-1",
            Epoch = 3,
            Tick = 5,
            StateVersion = 2,
            ModuleVersion = "module-1",
            ConfigHash = "cfg-1",
        }));
        Assert.True(await stream.MoveNextAsync(default));
        Assert.NotNull(stream.Current.Snapshot);
        Assert.Equal(2, stream.Current.Snapshot!.StateVersion);

        channel.Reply("sdk.sync", SyncBody("room_suspended",
            new RoomSuspendedFrame { Reason = "peer", RecoveryAttempt = 4 }));
        Assert.True(await stream.MoveNextAsync(default));
        Assert.NotNull(stream.Current.Suspended);
        Assert.False(await stream.MoveNextAsync(default));
    }

    [Fact]
    public async Task AnIntentSendRidesTheSameFraming()
    {
        var channel = new LoopbackChannel();
        var transport = new EnvelopeTransport(channel);

        var sendTask = transport.SendIntentAsync(new ClientIntent
        {
            SessionId = "sess-9",
            RoomId = "room-1",
            RoomEpoch = 3,
            CommandId = "c-1",
            PayloadJson = "{\"move\":1}",
        }, default);

        var sent = await channel.TakeSentAsync();
        Assert.Equal("room-1", sent.Body.GetProperty("roomId").GetString());
        channel.Reply("sdk.sync", new IntentReply { CommandId = "c-1", Sequence = 4, Duplicate = false });

        var reply = await sendTask;
        Assert.Equal("c-1", reply.CommandId);
        Assert.Equal(4, reply.Sequence);
    }

    // ------------------------------------------------------------------
    // Helpers.
    // ------------------------------------------------------------------

    private static JsonElement SyncBody(string kind, object member) =>
        JsonSerializer.SerializeToElement(new Dictionary<string, object> { ["kind"] = kind, [kind] = member },
            GodotProtocolJson.SerializerOptions);

    /// <summary>A loopback channel whose wire is the codec: sends serialize through
    /// <see cref="GodotEnvelopeCodec"/> to bytes and receives decode back from bytes,
    /// exactly the framing a socket speaks.</summary>
    private sealed class LoopbackChannel : IEnvelopeChannel
    {
        private readonly Channel<byte[]> _sent = Channel.CreateUnbounded<byte[]>();
        private readonly Channel<byte[]> _replies = Channel.CreateUnbounded<byte[]>();

        public bool IsOpen { get; private set; } = true;

        public async Task SendAsync(GodotEnvelope envelope, CancellationToken cancellationToken)
        {
            if (!IsOpen) throw new GodotTransportException("the wire is closed.");
            await _sent.Writer.WriteAsync(GodotEnvelopeCodec.Serialize(envelope), cancellationToken);
        }

        public async Task<GodotEnvelope> ReceiveAsync(CancellationToken cancellationToken)
        {
            var bytes = await _replies.Reader.ReadAsync(cancellationToken);
            return GodotEnvelopeCodec.Deserialize(bytes);
        }

        public Task CloseAsync()
        {
            IsOpen = false;
            _sent.Writer.Complete();
            _replies.Writer.Complete();
            return Task.CompletedTask;
        }

        public async Task<GodotEnvelope> TakeSentAsync()
        {
            var bytes = await _sent.Reader.ReadAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);
            return GodotEnvelopeCodec.Deserialize(bytes);
        }

        public void Reply(string wireType, object body)
        {
            var envelope = new GodotEnvelope(
                Type: wireType,
                MessageId: "reply-" + Guid.NewGuid().ToString("N"),
                SessionId: "session",
                CreatedTimeMs: 0,
                Body: JsonSerializer.SerializeToElement(body, GodotProtocolJson.SerializerOptions));
            _replies.Writer.TryWrite(GodotEnvelopeCodec.Serialize(envelope));
        }
    }
}

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Chorus.GodotSdk
{
    /// <summary>
    /// The Godot SDK's transport binding (EK-1): maps the <see cref="IChorusTransport"/>
    /// seam onto the Envelope framing, so the SDK's typed intents and answers ride
    /// <see cref="GodotEnvelope"/> bytes on an <see cref="IEnvelopeChannel"/>. This is the
    /// binding a game project plugs a socket into: the channel is the wire (Godot's
    /// WebSocketPeer or a managed transport), the transport is the protocol mapping, and
    /// the codec is the framing — which the golden test (EK-2) pins against the server's
    /// JsonEnvelopeCodec.
    ///
    /// The binding is request/response: one call sends one envelope and awaits the
    /// envelope that answers it, in order. It is not the edge's stream-owned downlink
    /// (Chorus.UnitySdk.Edge's stream resolves the intent receipts from the frames it
    /// reads); this batch's binding stays pull-style so a headless lane can drive it
    /// without a server, and the game's own subscription pump is what a production wire
    /// adds on top.
    /// </summary>
    public sealed class EnvelopeTransport : IChorusTransport
    {
        private readonly IEnvelopeChannel _channel;

        /// <summary>Creates the transport binding over one channel. The channel is the
        /// wire; the transport opens nothing itself.</summary>
        public EnvelopeTransport(IEnvelopeChannel channel)
        {
            _channel = channel ?? throw new ArgumentNullException(nameof(channel));
        }

        public async Task ConnectAsync(CancellationToken cancellationToken)
        {
            RequireOpen();
            // No opening handshake exists on this binding: the channel is already the
            // established wire, and the server's hello/version gate is the channel's
            // own concern.
        }

        public async Task<LoginAnswer> LoginAsync(LoginIntent intent, CancellationToken cancellationToken)
        {
            var body = await SendAsync(WireTypes.Login, intent, cancellationToken).ConfigureAwait(false);
            return body.Deserialize<LoginAnswer>(GodotProtocolJson.SerializerOptions)
                ?? throw new GodotTransportException("the login reply carried no answer.");
        }

        public async Task<string> StartGameplayAsync(string accessCredential, string gameId, string operationId,
            CancellationToken cancellationToken)
        {
            var body = await SendAsync(WireTypes.Login, new StartGameplayRequest
            {
                AccessCredential = accessCredential,
                GameId = gameId,
                OperationId = operationId,
            }, cancellationToken).ConfigureAwait(false);
            var reply = body.Deserialize<StartGameplayReply>(GodotProtocolJson.SerializerOptions);
            return reply?.BindingId ?? throw new GodotTransportException("the gameplay reply carried no binding.");
        }

        public async Task<MatchTicket> JoinMatchAsync(JoinMatchIntent intent, CancellationToken cancellationToken)
        {
            var body = await SendAsync(WireTypes.Sync, intent, cancellationToken).ConfigureAwait(false);
            return TicketFrom(body);
        }

        public async Task<MatchTicket> CancelMatchAsync(string operationId, string ticketId,
            CancellationToken cancellationToken)
        {
            var body = await SendAsync(WireTypes.Sync, new CancelMatchRequest
            {
                OperationId = operationId,
                TicketId = ticketId,
            }, cancellationToken).ConfigureAwait(false);
            return TicketFrom(body);
        }

        public async Task<JoinedRoom> AwaitRoomAsync(CancellationToken cancellationToken)
        {
            var body = await SendAsync(WireTypes.Sync, new AwaitRoomRequest(), cancellationToken)
                .ConfigureAwait(false);
            return body.Deserialize<JoinedRoom>(GodotProtocolJson.SerializerOptions)
                ?? throw new GodotTransportException("the room reply carried no entry.");
        }

        public async Task<ISyncStream> SubscribeAsync(string roomId, long epoch, long lastAppliedTick,
            long lastAppliedStateVersion, CancellationToken cancellationToken)
        {
            await SendAsync(WireTypes.Sync, new SubscribeRequest
            {
                RoomId = roomId,
                Epoch = epoch,
                LastAppliedTick = lastAppliedTick,
                LastAppliedStateVersion = lastAppliedStateVersion,
            }, cancellationToken).ConfigureAwait(false);
            // Pull-style: the caller advances the stream from its own loop, and the stream
            // pulls sdk.sync envelopes off the channel until the wire says the room is
            // over. No frames are read here, so the caller still owns the downlink.
            return new EnvelopeSyncStream(_channel);
        }

        public async Task<IntentReply> SendIntentAsync(ClientIntent intent, CancellationToken cancellationToken)
        {
            var body = await SendAsync(WireTypes.Sync, intent, cancellationToken).ConfigureAwait(false);
            return body.Deserialize<IntentReply>(GodotProtocolJson.SerializerOptions)
                ?? throw new GodotTransportException("the intent reply carried no reply.");
        }

        public async Task<LoginAnswer> ResumeAsync(ResumeIntent intent, CancellationToken cancellationToken)
        {
            var body = await SendAsync(WireTypes.Login, intent, cancellationToken).ConfigureAwait(false);
            return body.Deserialize<LoginAnswer>(GodotProtocolJson.SerializerOptions)
                ?? throw new GodotTransportException("the resume reply carried no answer.");
        }

        public async Task<JoinedRoom> AwaitRejoinedRoomAsync(CancellationToken cancellationToken)
        {
            var body = await SendAsync(WireTypes.Sync, new AwaitRoomRequest(), cancellationToken)
                .ConfigureAwait(false);
            return body.Deserialize<JoinedRoom>(GodotProtocolJson.SerializerOptions)
                ?? throw new GodotTransportException("the rejoined room reply carried no entry.");
        }

        public async Task LeaveRoundAsync(string roomId, string operationId, CancellationToken cancellationToken)
        {
            await SendAsync(WireTypes.Sync, new LeaveRoundRequest
            {
                RoomId = roomId,
                OperationId = operationId,
            }, cancellationToken).ConfigureAwait(false);
            // The wire acknowledges no leave: the call completes when the frame is sent.
        }

        public async Task LogoutAsync(string operationId, CancellationToken cancellationToken)
        {
            await SendAsync(WireTypes.Disconnect, new LogoutRequest { OperationId = operationId },
                cancellationToken).ConfigureAwait(false);
            // No ack exists; the game closes its own side with DisconnectAsync.
        }

        public async Task<LeaderboardAnswer> LeaderboardQueryAsync(LeaderboardQueryIntent intent,
            CancellationToken cancellationToken)
        {
            var body = await SendAsync(WireTypes.Leaderboard, intent, cancellationToken).ConfigureAwait(false);
            return body.Deserialize<LeaderboardAnswer>(GodotProtocolJson.SerializerOptions)
                ?? throw new GodotTransportException("the leaderboard reply carried no answer.");
        }

        public async Task CloseAsync()
        {
            await _channel.CloseAsync().ConfigureAwait(false);
        }

        /// <summary>Sends one wire message and answers the envelope that replies to it: the
        /// request's body is the message, the reply's body is the answer. The message id
        /// and session id are the transport's own per-call identities.</summary>
        private async Task<System.Text.Json.JsonElement> SendAsync(string wireType, object request,
            CancellationToken cancellationToken)
        {
            RequireOpen();
            var envelope = new GodotEnvelope(
                Type: wireType,
                MessageId: "msg-" + Guid.NewGuid().ToString("N"),
                SessionId: "session",
                CreatedTimeMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Body: System.Text.Json.JsonSerializer.SerializeToElement(request, GodotProtocolJson.SerializerOptions));
            await _channel.SendAsync(envelope, cancellationToken).ConfigureAwait(false);
            var reply = await _channel.ReceiveAsync(cancellationToken).ConfigureAwait(false);
            return reply.Body;
        }

        private static MatchTicket TicketFrom(System.Text.Json.JsonElement body)
        {
            return body.Deserialize<MatchTicket>(GodotProtocolJson.SerializerOptions)
                ?? throw new GodotTransportException("the match reply carried no ticket.");
        }

        private void RequireOpen()
        {
            if (!_channel.IsOpen)
            {
                throw new GodotTransportException("the wire is closed.");
            }
        }

        /// <summary>One pull-style subscription over the channel's downlink (EK-1): each
        /// advance pulls one sdk.sync envelope and maps its body onto a
        /// <see cref="SyncFrame"/>. The park notice ends the subscription — it is the
        /// room's last frame, delivered as the stream's last answer.</summary>
        private sealed class EnvelopeSyncStream : ISyncStream
        {
            private readonly IEnvelopeChannel _channel;
            private bool _terminal;

            public EnvelopeSyncStream(IEnvelopeChannel channel) => _channel = channel;

            public SyncFrame Current { get; private set; } = new SyncFrame();

            public async Task<bool> MoveNextAsync(CancellationToken cancellationToken)
            {
                if (_terminal)
                {
                    // The room's story is told: the park notice was the last frame this
                    // subscription carries.
                    return false;
                }

                var envelope = await _channel.ReceiveAsync(cancellationToken).ConfigureAwait(false);
                var frame = Map(envelope);
                if (frame.Suspended is not null)
                {
                    // The park notice is the subscription's last frame: the recovered room
                    // returns on a bumped epoch, which is a new subscribe, not a frame
                    // behind this one.
                    _terminal = true;
                }

                Current = frame;
                return true;
            }

            private static SyncFrame Map(GodotEnvelope envelope)
            {
                var body = envelope.Body.Deserialize<SyncEnvelopeBody>(GodotProtocolJson.SerializerOptions)
                    ?? throw new GodotTransportException("the sync envelope carried no frame.");
                var frame = new SyncFrame
                {
                    Kind = body.Kind ?? SyncFrameKind.Snapshot,
                    Snapshot = body.Snapshot,
                    Delta = body.Delta,
                    RoundResult = body.RoundResult,
                    ResyncRequired = body.ResyncRequired,
                    Suspended = body.Suspended,
                };
                return frame;
            }

            /// <summary>The sync lane's wire body: the frame's kind word plus the member
            /// that carries it. Names the wire fields explicitly — the kind word is the
            /// SyncMessage oneof member, not a property.</summary>
            private sealed class SyncEnvelopeBody
            {
                [System.Text.Json.Serialization.JsonPropertyName("kind")]
                public string? Kind { get; set; }

                [System.Text.Json.Serialization.JsonPropertyName("snapshot")]
                public SnapshotFrame? Snapshot { get; set; }

                [System.Text.Json.Serialization.JsonPropertyName("delta")]
                public DeltaFrame? Delta { get; set; }

                [System.Text.Json.Serialization.JsonPropertyName("round_result")]
                public RoundResultFrame? RoundResult { get; set; }

                [System.Text.Json.Serialization.JsonPropertyName("resync_required")]
                public ResyncRequiredFrame? ResyncRequired { get; set; }

                [System.Text.Json.Serialization.JsonPropertyName("room_suspended")]
                public RoomSuspendedFrame? Suspended { get; set; }
            }
        }

        private sealed class StartGameplayRequest
        {
            public string AccessCredential { get; set; } = string.Empty;
            public string GameId { get; set; } = string.Empty;
            public string OperationId { get; set; } = string.Empty;
        }

        private sealed class StartGameplayReply
        {
            public string BindingId { get; set; } = string.Empty;
        }

        private sealed class CancelMatchRequest
        {
            public string OperationId { get; set; } = string.Empty;
            public string TicketId { get; set; } = string.Empty;
        }

        private sealed class AwaitRoomRequest
        {
            // Empty request: the wire names the room the mount is in.
        }

        private sealed class SubscribeRequest
        {
            public string RoomId { get; set; } = string.Empty;
            public long Epoch { get; set; }
            public long LastAppliedTick { get; set; }
            public long LastAppliedStateVersion { get; set; }
        }

        private sealed class LeaveRoundRequest
        {
            public string RoomId { get; set; } = string.Empty;
            public string OperationId { get; set; } = string.Empty;
        }

        private sealed class LogoutRequest
        {
            public string OperationId { get; set; } = string.Empty;
        }
    }

    /// <summary>A transport-level wire failure (EK-1): the wire is closed, or the peer
    /// answered without the shape this call expects. Distinct from a protocol violation
    /// in a frame's fields — that is the applier's
    /// <see cref="SyncDecisionKind.Invalid"/>.</summary>
    public sealed class GodotTransportException : InvalidOperationException
    {
        public GodotTransportException(string message) : base(message) { }
    }
}

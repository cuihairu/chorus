using System;
using System.Collections.Generic;
using System.Net.Security;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Chorus.UnitySdk;

namespace Chorus.UnitySdk.Edge
{
    /// <summary>
    /// The Chorus edge's wire (FIX-U3D-07): JSON over WSS, exactly the protocol the
    /// gateway speaks — hello first with the protocol version, loginWithPlatformLaunch
    /// answered by loginReady, startGameplay answered by gameplayStatus, error frames
    /// surfaced as exceptions. The gateway stamps the tenant and the connection
    /// identities; the SDK never invents them.
    ///
    /// The login link drives the transport strictly sequentially (connect → login →
    /// gameplay start), so one frame queue serves every waiter; frames this link does
    /// not claim (session facts pushed early, ping replies) stay queued for the links
    /// FIX-U3D-09/10 wire onto the same transport.
    /// </summary>
    public sealed class EdgeChorusTransport : IChorusTransport
    {
        private const int ProtocolVersion = 1;

        private readonly string _endpoint;
        private readonly string _clientBuild;
        private readonly RemoteCertificateValidationCallback? _certificateValidationOverride;
        private ClientWebSocket? _socket;
        private readonly Queue<(string Type, string Json)> _unclaimed = new();

        /// <summary>Creates the edge transport for one configuration's endpoint. The
        /// certificate validation override exists for test and self-hosted endpoints
        /// whose certificates chain to a private root; production passes null and gets
        /// the platform's own validation.</summary>
        public EdgeChorusTransport(SdkConfiguration configuration,
            RemoteCertificateValidationCallback? certificateValidationOverride = null,
            string? clientBuild = null)
        {
            if (configuration is null) throw new ArgumentNullException(nameof(configuration));
            _endpoint = WsEndpointFrom(configuration.Endpoint);
            _certificateValidationOverride = certificateValidationOverride;
            _clientBuild = clientBuild ?? "chorus-unity-sdk";
        }

        /// <summary>Derives the WSS url from the configured public endpoint: https
        /// becomes wss (and http ws for self-hosted plaintext), the /ws gateway path is
        /// appended when the endpoint names none. An unknown scheme is refused.</summary>
        public static string WsEndpointFrom(string endpoint)
        {
            if (string.IsNullOrWhiteSpace(endpoint))
                throw new ArgumentException("the endpoint is empty.", nameof(endpoint));
            if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
                throw new ArgumentException($"the endpoint '{endpoint}' is not an absolute url.", nameof(endpoint));
            var scheme = uri.Scheme == "https" || uri.Scheme == "wss" ? "wss"
                : uri.Scheme == "http" || uri.Scheme == "ws" ? "ws"
                : throw new ArgumentException(
                    $"the endpoint scheme '{uri.Scheme}' is not a chorus gateway endpoint (use https or wss).",
                    nameof(endpoint));
            var path = uri.AbsolutePath;
            if (string.IsNullOrEmpty(path) || path == "/")
                path = "/ws";
            return scheme + "://" + uri.Authority + path;
        }

        public async Task ConnectAsync(CancellationToken cancellationToken)
        {
            if (_socket is not null)
                throw new InvalidOperationException("the transport is already connected.");
            var socket = new ClientWebSocket();
            if (_certificateValidationOverride is not null)
                socket.Options.RemoteCertificateValidationCallback = _certificateValidationOverride;
            await socket.ConnectAsync(new Uri(_endpoint), cancellationToken).ConfigureAwait(false);
            // The hello gates everything that follows; the gateway answers nothing for it.
            await SendAsync(socket, new HelloFrame
            {
                ProtocolVersion = ProtocolVersion,
                ClientBuild = _clientBuild,
            }, cancellationToken).ConfigureAwait(false);
            _socket = socket;
        }

        public async Task<LoginAnswer> LoginAsync(LoginIntent intent, CancellationToken cancellationToken)
        {
            var socket = RequireSocket();
            await SendAsync(socket, new LoginFrame
            {
                Platform = intent.Platform,
                AppId = intent.AppId,
                LaunchCredential = intent.LaunchCredential,
                LoginAttemptId = intent.LoginAttemptId,
                NextResumeSecret = intent.NextResumeSecret,
            }, cancellationToken).ConfigureAwait(false);
            var json = await AwaitFrameAsync(socket, "loginReady", cancellationToken).ConfigureAwait(false);
            var ready = Deserialize<LoginReadyFrame>(json);
            // The edge carries displacement as sessionReplaced (then closes), not as a
            // duplicate flag on the ready frame — the flag stays false on this wire.
            return new LoginAnswer
            {
                StreamerId = ready.StreamerId,
                SessionId = ready.SessionId,
                SessionVersion = ready.SessionVersion,
                AccessCredential = ready.AccessCredential,
                AccessExpiresAtUnixMs = ready.AccessExpiresAt,
                Duplicate = false,
                ResumeRegistered = ready.ResumeRegistered,
            };
        }

        public async Task<string> StartGameplayAsync(string accessCredential, string gameId, string operationId,
            CancellationToken cancellationToken)
        {
            var socket = RequireSocket();
            await SendAsync(socket, new StartGameplayFrame
            {
                OperationId = operationId,
                GameId = gameId,
            }, cancellationToken).ConfigureAwait(false);
            var json = await AwaitFrameAsync(socket, "gameplayStatus", cancellationToken).ConfigureAwait(false);
            var status = Deserialize<GameplayStatusFrame>(json);
            if (string.IsNullOrEmpty(status.BindingId))
                throw new EdgeProtocolException("gameplayStatus",
                    "the edge answered a gameplay status without a binding id.");
            return status.BindingId;
        }

        public async Task<MatchTicket> JoinMatchAsync(JoinMatchIntent intent, CancellationToken cancellationToken)
        {
            var socket = RequireSocket();
            await SendAsync(socket, new JoinQueueFrame
            {
                OperationId = intent.OperationId,
                QueueType = intent.QueueType,
                Options = intent.Options,
            }, cancellationToken).ConfigureAwait(false);
            var json = await AwaitFrameAsync(socket, "matchStatus", cancellationToken).ConfigureAwait(false);
            return TicketFrom(Deserialize<MatchStatusFrame>(json));
        }

        public async Task<MatchTicket> CancelMatchAsync(string operationId, string ticketId,
            CancellationToken cancellationToken)
        {
            var socket = RequireSocket();
            await SendAsync(socket, new CancelQueueFrame
            {
                OperationId = operationId,
                TicketId = ticketId,
            }, cancellationToken).ConfigureAwait(false);
            var json = await AwaitFrameAsync(socket, "matchStatus", cancellationToken).ConfigureAwait(false);
            return TicketFrom(Deserialize<MatchStatusFrame>(json));
        }

        public async Task<JoinedRoom> AwaitRoomAsync(CancellationToken cancellationToken)
        {
            var socket = RequireSocket();
            // The queue answers queued, then the coordinator's commit arrives as a push;
            // a cancelled ticket is terminal and refuses instead of waiting forever.
            string? ticketId = null;
            string? reservationId = null;
            string? roomId = null;
            while (true)
            {
                var json = await AwaitFrameAsync(socket, "matchStatus", cancellationToken).ConfigureAwait(false);
                var status = Deserialize<MatchStatusFrame>(json);
                ticketId = status.TicketId;
                if (status.State == "Matched")
                {
                    roomId = status.RoomId;
                    reservationId = status.ReservationId;
                    break;
                }
                if (status.State == "Cancelled")
                    throw new EdgeProtocolException("matchCancelled", "the ticket was cancelled while awaiting the room.");
            }
            if (string.IsNullOrEmpty(roomId))
                throw new EdgeProtocolException("matchStatus", "the edge answered a match without a room id.");
            var joined = Deserialize<RoomJoinedFrame>(
                await AwaitFrameAsync(socket, "roomJoined", cancellationToken).ConfigureAwait(false));
            return new JoinedRoom
            {
                RoomId = joined.RoomId,
                RoomEpoch = joined.RoomEpoch,
                Participation = joined.Participation,
                ModuleVersion = joined.ModuleVersion,
                ConfigHash = joined.ConfigHash,
                TicketId = ticketId ?? string.Empty,
                ReservationId = reservationId ?? string.Empty,
            };
        }

        private static MatchTicket TicketFrom(MatchStatusFrame status) => new()
        {
            TicketId = status.TicketId,
            State = status.State,
            RoomId = status.RoomId ?? string.Empty,
            ReservationId = status.ReservationId ?? string.Empty,
        };

        public async Task CloseAsync()
        {
            var socket = _socket;
            _socket = null;
            if (socket is null) return;
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "client", timeout.Token)
                    .ConfigureAwait(false);
            }
            catch (WebSocketException)
            {
                // The server closed first — refusals and logouts close abruptly and the
                // close handshake never completes. A failed close must never mask the
                // link's real outcome (an exception thrown here would replace the
                // caller's); disposal finishes the job.
            }
            catch (OperationCanceledException)
            {
                // The graceful close raced its five-second budget; disposal finishes it.
            }
            finally
            {
                socket.Dispose();
            }
        }

        private ClientWebSocket RequireSocket() => _socket
            ?? throw new InvalidOperationException("the transport is not connected; connect before login.");

        /// <summary>Reads frames until the awaited type arrives. Error frames surface as
        /// the protocol exception carrying the edge's code; any other frame stays
        /// queued for the link that owns it.</summary>
        private async Task<string> AwaitFrameAsync(ClientWebSocket socket, string awaitedType,
            CancellationToken cancellationToken)
        {
            while (true)
            {
                var json = TakeUnclaimed(awaitedType);
                if (json is not null) return json;
                var text = await ReceiveTextAsync(socket, cancellationToken).ConfigureAwait(false);
                var type = FrameType(text);
                if (type == "error")
                {
                    var error = Deserialize<ErrorFrame>(text);
                    throw new EdgeProtocolException(error.Code, error.Message);
                }
                if (type == awaitedType) return text;
                _unclaimed.Enqueue((type, text));
            }
        }

        private string? TakeUnclaimed(string awaitedType)
        {
            foreach (var frame in _unclaimed)
            {
                if (frame.Type == awaitedType)
                {
                    _unclaimed.Clear();
                    return frame.Json;
                }
            }
            return null;
        }

        private static string FrameType(string text)
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var type)
                || type.ValueKind != JsonValueKind.String)
                throw new EdgeProtocolException("invalidFrame", "the edge sent a frame without a type.");
            return type.GetString()!;
        }

        private static T Deserialize<T>(string text)
        {
            var value = JsonSerializer.Deserialize<T>(text);
            if (value is null)
                throw new EdgeProtocolException("invalidFrame", "the edge sent an empty frame.");
            return value;
        }

        private static async Task SendAsync(ClientWebSocket socket, object frame, CancellationToken cancellationToken)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(frame);
            await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, cancellationToken)
                .ConfigureAwait(false);
        }

        private static async Task<string> ReceiveTextAsync(WebSocket socket, CancellationToken cancellationToken)
        {
            var buffer = new byte[16 * 1024];
            var builder = new StringBuilder();
            while (true)
            {
                var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken)
                    .ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                    throw new EdgeProtocolException("closed", "the edge closed the connection mid-link.");
                builder.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                if (result.EndOfMessage)
                    return builder.ToString();
            }
        }

        private sealed class HelloFrame
        {
            [JsonPropertyName("type")]
            public string Type => "hello";

            [JsonPropertyName("protocolVersion")]
            public int ProtocolVersion { get; set; }

            [JsonPropertyName("clientBuild")]
            public string ClientBuild { get; set; } = string.Empty;
        }

        private sealed class LoginFrame
        {
            [JsonPropertyName("type")]
            public string Type => "loginWithPlatformLaunch";

            [JsonPropertyName("platform")]
            public string Platform { get; set; } = string.Empty;

            [JsonPropertyName("appId")]
            public string AppId { get; set; } = string.Empty;

            [JsonPropertyName("launchCredential")]
            public string LaunchCredential { get; set; } = string.Empty;

            [JsonPropertyName("loginAttemptId")]
            public string LoginAttemptId { get; set; } = string.Empty;

            [JsonPropertyName("nextResumeSecret")]
            public byte[] NextResumeSecret { get; set; } = Array.Empty<byte>();
        }

        private sealed class StartGameplayFrame
        {
            [JsonPropertyName("type")]
            public string Type => "startGameplay";

            [JsonPropertyName("operationId")]
            public string OperationId { get; set; } = string.Empty;

            [JsonPropertyName("gameId")]
            public string GameId { get; set; } = string.Empty;
        }

        private sealed class LoginReadyFrame
        {
            [JsonPropertyName("streamerId")]
            public string StreamerId { get; set; } = string.Empty;

            [JsonPropertyName("sessionId")]
            public string SessionId { get; set; } = string.Empty;

            [JsonPropertyName("sessionVersion")]
            public long SessionVersion { get; set; }

            [JsonPropertyName("accessCredential")]
            public string AccessCredential { get; set; } = string.Empty;

            [JsonPropertyName("accessExpiresAt")]
            public long AccessExpiresAt { get; set; }

            [JsonPropertyName("resumeRegistered")]
            public bool ResumeRegistered { get; set; }
        }

        private sealed class GameplayStatusFrame
        {
            [JsonPropertyName("bindingId")]
            public string BindingId { get; set; } = string.Empty;

            [JsonPropertyName("generation")]
            public long Generation { get; set; }

            [JsonPropertyName("state")]
            public string State { get; set; } = string.Empty;

            [JsonPropertyName("reason")]
            public string? Reason { get; set; }
        }

        private sealed class ErrorFrame
        {
            [JsonPropertyName("code")]
            public string Code { get; set; } = string.Empty;

            [JsonPropertyName("message")]
            public string Message { get; set; } = string.Empty;
        }

        private sealed class JoinQueueFrame
        {
            [JsonPropertyName("type")]
            public string Type => "joinQueue";

            [JsonPropertyName("operationId")]
            public string OperationId { get; set; } = string.Empty;

            [JsonPropertyName("queueType")]
            public string QueueType { get; set; } = string.Empty;

            [JsonPropertyName("options")]
            public string Options { get; set; } = string.Empty;
        }

        private sealed class CancelQueueFrame
        {
            [JsonPropertyName("type")]
            public string Type => "cancelQueue";

            [JsonPropertyName("operationId")]
            public string OperationId { get; set; } = string.Empty;

            [JsonPropertyName("ticketId")]
            public string TicketId { get; set; } = string.Empty;
        }

        private sealed class MatchStatusFrame
        {
            [JsonPropertyName("ticketId")]
            public string TicketId { get; set; } = string.Empty;

            [JsonPropertyName("state")]
            public string State { get; set; } = string.Empty;

            [JsonPropertyName("reservationId")]
            public string? ReservationId { get; set; }

            [JsonPropertyName("roomId")]
            public string? RoomId { get; set; }
        }

        private sealed class RoomJoinedFrame
        {
            [JsonPropertyName("roomId")]
            public string RoomId { get; set; } = string.Empty;

            [JsonPropertyName("roomEpoch")]
            public long RoomEpoch { get; set; }

            [JsonPropertyName("participation")]
            public string Participation { get; set; } = string.Empty;

            [JsonPropertyName("moduleVersion")]
            public string ModuleVersion { get; set; } = string.Empty;

            [JsonPropertyName("configHash")]
            public string ConfigHash { get; set; } = string.Empty;
        }
    }

    /// <summary>A visible refusal from the edge: the code is the protocol's own error
    /// vocabulary, the message short and safe to show.</summary>
    public sealed class EdgeProtocolException : InvalidOperationException
    {
        public EdgeProtocolException(string code, string message) : base(message)
        {
            Code = code;
        }

        /// <summary>The edge's error code for this refusal.</summary>
        public string Code { get; }
    }
}

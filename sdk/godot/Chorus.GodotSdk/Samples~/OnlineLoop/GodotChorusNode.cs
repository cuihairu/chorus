using System;
using System.Threading;
using System.Threading.Tasks;
using Chorus.GodotSdk;
using Godot;

// This file lives in Samples~ and is NOT part of the SDK assembly: it needs
// GodotSharp, which the leaf deliberately does not reference. A Godot 4 .NET
// project references Chorus.GodotSdk.dll alongside this file and gets one Node
// that speaks the whole session lifecycle as Godot signals.

namespace Chorus.GodotSdk.Samples
{
    /// <summary>
    /// The engine bridge (EK-1): the leaf's C# event surface, re-emitted as Godot
    /// signals so GDScript can connect to them. The leaf stays engine-free; only
    /// this Node touches GodotSharp. The wire and the platform mount are the game
    /// project's own (the channel's socket, the platform's launch flow) — the
    /// sample keeps them as thin stubs the reader fills in.
    /// </summary>
    public partial class GodotChorusNode : Node
    {
        [Signal] public delegate void RoomJoinedEventHandler(string roomId, long epoch);
        [Signal] public delegate void MatchJoinedEventHandler(string ticketId, string roomId);
        [Signal] public delegate void DisconnectedEventHandler();
        [Signal] public delegate void WireErrorEventHandler(string message);

        private ChorusGodotSdk? _sdk;

        public override async void _Ready()
        {
            var sdk = new ChorusGodotSdk(cfg => new EnvelopeTransport(new SampleChannel()));
            _sdk = sdk;

            // Subscribe before the connect so the session's own events are wired.
            sdk.SessionEstablished += session =>
            {
                session.RoomJoined += room => EmitSignal(SignalName.RoomJoined, room.RoomId, room.RoomEpoch);
                session.MatchJoined += ticket => EmitSignal(SignalName.MatchJoined, ticket.TicketId, ticket.RoomId);
                session.Disconnected += () => EmitSignal(SignalName.Disconnected);
            };

            try
            {
                var session = await sdk.ConnectAsync(
                    new SdkConfiguration
                    {
                        Platform = "douyin",
                        AppId = "app-1",
                        Tenant = "tenant-a",
                        GameId = "game-1",
                        Endpoint = "wss://gateway.example/ws",
                        Role = "player",
                    },
                    new SamplePlatformAdapter(),
                    CancellationToken.None);
                await session.JoinMatchAsync("standard", null, CancellationToken.None);
                var room = await session.AwaitRoomAsync(CancellationToken.None);
                var stream = await session.SubscribeAsync(
                    room.RoomId, room.RoomEpoch, -1, -1, CancellationToken.None);

                var applier = new SyncApplier(room.RoomId, room.RoomEpoch, DeltaContractMode.Full);
                while (await stream.MoveNextAsync(CancellationToken.None))
                {
                    var decision = applier.Decide(stream.Current);
                    if (decision.Kind == SyncDecisionKind.ApplySnapshot
                        || decision.Kind == SyncDecisionKind.ApplyDelta)
                    {
                        GD.Print("state version ", applier.Status.StateVersion);
                    }
                }
            }
            catch (Exception error)
            {
                EmitSignal(SignalName.WireError, error.Message);
            }
        }
    }

    /// <summary>The channel the transport speaks over (EK-1): one envelope per text
    /// frame, both directions. The socket (Godot's WebSocketPeer, or a managed
    /// transport) is the game's own; this stub is the shape to fill in.</summary>
    public sealed class SampleChannel : IEnvelopeChannel
    {
        public bool IsOpen => true;

        public Task SendAsync(GodotEnvelope envelope, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task<GodotEnvelope> ReceiveAsync(CancellationToken cancellationToken)
            => throw new NotSupportedException("fill in the socket's receive loop.");

        public Task CloseAsync() => Task.CompletedTask;
    }

    /// <summary>A stand-in platform adapter: the game's own launch flow feeds the
    /// mount. The real one is the game project's concern (a Douyin HTTP launch, a
    /// Kuaishou launch-parameter parse), not the SDK's.</summary>
    public sealed class SamplePlatformAdapter : IPlatformAdapter
    {
        public string PlatformName => "douyin";

        public Task<PlatformMount> MountAsync(CancellationToken cancellationToken) => Task.FromResult(new PlatformMount
        {
            Platform = "douyin",
            AppId = "app-1",
            Role = "player",
            SessionContextJson = "{}",
        });

        public Task ShutdownAsync() => Task.CompletedTask;
    }
}

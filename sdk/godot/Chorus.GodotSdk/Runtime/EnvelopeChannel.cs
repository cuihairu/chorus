using System;
using System.Threading;
using System.Threading.Tasks;

namespace Chorus.GodotSdk
{
    /// <summary>
    /// The channel the Envelope transport speaks over (EK-1): one direction in, one
    /// out, each message one Envelope. The channel is deliberately engine-shaped — a
    /// game project plugs in its socket (Godot's WebSocketPeer, or a managed transport)
    /// — and the transport stays engine-free. The leaf ships no channel: the game owns
    /// the wire, exactly as the Unity SDK keeps its edge wire in Chorus.UnitySdk.Edge.
    /// </summary>
    public interface IEnvelopeChannel
    {
        /// <summary>Whether the wire is open. A closed channel's sends and receives fail.</summary>
        bool IsOpen { get; }

        /// <summary>Sends one envelope out on the wire.</summary>
        Task SendAsync(GodotEnvelope envelope, CancellationToken cancellationToken);

        /// <summary>Answers one envelope from the wire.</summary>
        Task<GodotEnvelope> ReceiveAsync(CancellationToken cancellationToken);

        /// <summary>Closes the wire.</summary>
        Task CloseAsync();
    }
}

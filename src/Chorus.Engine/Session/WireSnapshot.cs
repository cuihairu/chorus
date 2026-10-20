using System.Text.Json;
using Chorus.Protocol;
using Chorus.Protocol.Messages;

namespace Chorus.Engine.Session;

/// <summary>
/// A snapshot paired with its pre-encoded wire bytes (the exact JSON the
/// streamer-ws clients receive). Serialized once per tick on the broadcast
/// path so client count no longer multiplies JSON serialization; each client
/// just writes the same bytes to its transport.
/// </summary>
/// State is written inline into the payload stream: the old shape built a
/// JsonElement DOM first and serialized it again, so the tick body visited
/// the state twice per snapshot. The inline write keeps the wire bytes
/// identical and cuts the passes in half.
public sealed record WireSnapshot(SessionSnapshot Snapshot, byte[] EncodedJson)
{
    public static WireSnapshot Encode(SessionSnapshot snapshot)
    {
        var payload = new ServerSnapshot(
            Type: ProtocolTypes.Snapshot,
            SessionId: snapshot.SessionId,
            TickId: snapshot.TickId,
            ServerTimeMs: snapshot.ServerTimeMs,
            State: snapshot.State,
            Forced: snapshot.Forced,
            Reason: snapshot.Reason,
            TargetClientId: snapshot.TargetClientId,
            ClientLagTicks: snapshot.ClientLagTicks,
            ClientLastAckTickId: snapshot.ClientLastAckTickId,
            V: snapshot.V,
            FrameEvents: snapshot.FrameEvents is { Count: > 0 } ? new List<FrameEvent>(snapshot.FrameEvents) : null);

        return new WireSnapshot(
            snapshot,
            JsonSerializer.SerializeToUtf8Bytes(payload, ProtocolJson.SerializerOptions));
    }
}

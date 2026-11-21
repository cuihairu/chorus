using System.Text.Json;

namespace Chorus.GodotSdk
{
    /// <summary>
    /// The wire envelope (EK-1): a mirror of Chorus.Messaging.Envelope, restated in
    /// the leaf so the Godot package carries no Chorus reference. <see cref="MessageId"/>
    /// identifies one delivery; <see cref="SourceEventId"/> optionally carries the
    /// platform event id this delivery was born from; <see cref="Body"/> is the typed
    /// message payload as opaque JSON. The Envelope codec pins this shape against the
    /// server's JsonEnvelopeCodec byte-for-byte (EK-2), so the field names, order and
    /// null handling must match the reference exactly.
    /// </summary>
    public sealed record GodotEnvelope(
        string Type,
        string MessageId,
        string SessionId,
        long CreatedTimeMs,
        JsonElement Body,
        string? SourceEventId = null,
        long? AuthorityEpoch = null);
}

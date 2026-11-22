using System.Text;
using System.Text.Json;
using Chorus.GodotSdk;
using Chorus.Messaging;
using Xunit;

namespace Chorus.GodotSdk.Tests;

/// <summary>
/// The Godot leaf's Envelope codec (EK-1): it must reproduce the reference codec's
/// wire byte-for-byte — same field names, same null handling, same body passthrough —
/// so the golden set in the cross-engine tests stays green and any drift on either
/// side turns red. These tests pin the codec against <see cref="JsonEnvelopeCodec"/>
/// directly (the reference), not against a golden byte file, so they fail on the exact
/// byte a change would move.
/// </summary>
public sealed class GodotEnvelopeCodecTests
{
    // ------------------------------------------------------------------
    // Round trip (leaf only).
    // ------------------------------------------------------------------

    [Fact]
    public void SerializeThenDeserializeAnswersEqualFields()
    {
        var original = Canonical("sdk.sync", null, null);

        var bytes = GodotEnvelopeCodec.Serialize(original);
        var round = GodotEnvelopeCodec.Deserialize(bytes);

        Assert.Equal(original.Type, round.Type);
        Assert.Equal(original.MessageId, round.MessageId);
        Assert.Equal(original.SessionId, round.SessionId);
        Assert.Equal(original.CreatedTimeMs, round.CreatedTimeMs);
        Assert.Equal(original.SourceEventId, round.SourceEventId);
        Assert.Equal(original.AuthorityEpoch, round.AuthorityEpoch);
        Assert.True(JsonElement.DeepEquals(original.Body, round.Body),
            "the body must survive the round trip byte for byte.");
    }

    [Fact]
    public void NullOptionalFieldsAreOmittedFromTheWire()
    {
        var json = Encoding.UTF8.GetString(GodotEnvelopeCodec.Serialize(Canonical("sdk.login", null, null)));
        using var doc = JsonDocument.Parse(json);

        // The reference codec writes WhenWritingNull: sourceEventId/authorityEpoch stay
        // absent while the always-present five fields ride.
        Assert.False(doc.RootElement.TryGetProperty("sourceEventId", out _));
        Assert.False(doc.RootElement.TryGetProperty("authorityEpoch", out _));
        Assert.True(doc.RootElement.TryGetProperty("type", out _));
        Assert.True(doc.RootElement.TryGetProperty("body", out _));
    }

    [Fact]
    public void SetOptionalFieldsRideTheWire()
    {
        var json = Encoding.UTF8.GetString(GodotEnvelopeCodec.Serialize(
            Canonical("sdk.leaderboard", "evt-1", 7)));
        using var doc = JsonDocument.Parse(json);

        Assert.Equal("evt-1", doc.RootElement.GetProperty("sourceEventId").GetString());
        Assert.Equal(7, doc.RootElement.GetProperty("authorityEpoch").GetInt64());
    }

    // ------------------------------------------------------------------
    // Byte parity with the reference codec (EK-2's subject, asserted here too).
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("sdk.login", null, null)]
    [InlineData("sdk.sync", "evt-9", 3L)]
    [InlineData("sdk.leaderboard", null, 11L)]
    [InlineData("sdk.disconnect", "evt-0", null)]
    public void SameEnvelopeSerializesToIdenticalBytes(string type, string? sourceEventId, long? authorityEpoch)
    {
        var godot = Canonical(type, sourceEventId, authorityEpoch);
        var reference = new Envelope(
            Type: godot.Type,
            MessageId: godot.MessageId,
            SessionId: godot.SessionId,
            CreatedTimeMs: godot.CreatedTimeMs,
            Body: godot.Body.Clone(),
            SourceEventId: godot.SourceEventId,
            AuthorityEpoch: godot.AuthorityEpoch);

        var godotBytes = GodotEnvelopeCodec.Serialize(godot);
        var referenceBytes = JsonEnvelopeCodec.Serialize(reference);

        Assert.Equal(referenceBytes, godotBytes);
    }

    [Fact]
    public void DeserializeCrossRecognizesTheReferenceBytes()
    {
        var reference = new Envelope("sdk.sync", "m-1", "s-1", 1234,
            Body("{\"roomId\":\"room-1\",\"epoch\":3}"), "evt-2", 5);
        var bytes = JsonEnvelopeCodec.Serialize(reference);

        var godot = GodotEnvelopeCodec.Deserialize(bytes);

        Assert.Equal("sdk.sync", godot.Type);
        Assert.Equal("m-1", godot.MessageId);
        Assert.Equal("s-1", godot.SessionId);
        Assert.Equal(1234, godot.CreatedTimeMs);
        Assert.Equal("evt-2", godot.SourceEventId);
        Assert.Equal(5, godot.AuthorityEpoch);
        Assert.True(JsonElement.DeepEquals(Body("{\"roomId\":\"room-1\",\"epoch\":3}"), godot.Body));
    }

    // ------------------------------------------------------------------
    // Helpers: one canonical envelope and its body JSON.
    // ------------------------------------------------------------------

    /// <summary>One canonical envelope carrying a realistic body. The body is a
    /// representative wire payload (a sync frame's fields), so the parity assertion
    /// exercises real shape, not an empty object.</summary>
    private static GodotEnvelope Canonical(string type, string? sourceEventId, long? authorityEpoch)
        => new GodotEnvelope(
            Type: type,
            MessageId: "msg-1001",
            SessionId: "sess-77",
            CreatedTimeMs: 1_769_000_000_000,
            Body: Body("{\"roomId\":\"room-1\",\"epoch\":3,\"stateVersion\":2}"),
            SourceEventId: sourceEventId,
            AuthorityEpoch: authorityEpoch);

    private static JsonElement Body(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }
}

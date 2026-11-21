using System.Text;
using System.Text.Json;
using Chorus.GodotSdk;
using Chorus.Messaging;
using Xunit;

namespace Chorus.Engine.Tests.CrossEngine;

/// <summary>
/// The cross-engine consistency core (EK-2): the golden Envelope set pins the wire
/// format so Unity(C#)/the reference codec and the Godot codec agree byte-for-byte.
/// One side changing the wire (a field name, the null handling, the body passthrough)
/// without the other turns this red — that is the 防漂移 gate. The Unity side here is
/// the reference <see cref="JsonEnvelopeCodec"/> (Chorus.Messaging), the server's own
/// codec; the Godot side is <see cref="GodotEnvelopeCodec"/> (Chorus.GodotSdk).
/// </summary>
public sealed class CrossEngineEnvelopeGoldenTests
{
    [Theory]
    [MemberData(nameof(GoldenData))]
    public void BothCodecsSerializeTheSameEnvelopeToIdenticalBytes(GoldenEnvelope golden)
    {
        var godot = ToGodot(golden);
        var reference = ToReference(golden);

        var godotBytes = GodotEnvelopeCodec.Serialize(godot);
        var referenceBytes = JsonEnvelopeCodec.Serialize(reference);

        Assert.Equal(referenceBytes, godotBytes);
    }

    [Theory]
    [MemberData(nameof(GoldenData))]
    public void TheGodotCodecReadsTheReferenceBytesToEqualFields(GoldenEnvelope golden)
    {
        var reference = ToReference(golden);
        var bytes = JsonEnvelopeCodec.Serialize(reference);

        var godot = GodotEnvelopeCodec.Deserialize(bytes);

        AssertGoldenFields(golden, godot);
    }

    [Theory]
    [MemberData(nameof(GoldenData))]
    public void TheReferenceCodecReadsTheGodotBytesToEqualFields(GoldenEnvelope golden)
    {
        var godot = ToGodot(golden);
        var bytes = GodotEnvelopeCodec.Serialize(godot);

        var reference = JsonEnvelopeCodec.Deserialize(bytes);

        Assert.Equal(golden.Type, reference.Type);
        Assert.Equal(golden.MessageId, reference.MessageId);
        Assert.Equal(golden.SessionId, reference.SessionId);
        Assert.Equal(golden.CreatedTimeMs, reference.CreatedTimeMs);
        Assert.Equal(golden.SourceEventId, reference.SourceEventId);
        Assert.Equal(golden.AuthorityEpoch, reference.AuthorityEpoch);
        Assert.True(JsonElement.DeepEquals(Body(golden.BodyJson), reference.Body),
            $"the {golden.Name} body must survive the wire byte for byte.");
    }

    [Theory]
    [MemberData(nameof(GoldenData))]
    public void TheBodyRidesAsOpaqueJsonNotAsAnEscapedString(GoldenEnvelope golden)
    {
        // The body is a JSON object on the wire, not a string field: the golden bytes
        // must contain the body's fields at the top of the body, not quoted. This is
        // the JsonElement passthrough contract — a serializer that stringified the body
        // would fail here and on the byte-parity facts above.
        var bytes = GodotEnvelopeCodec.Serialize(ToGodot(golden));
        using var doc = JsonDocument.Parse(bytes);
        var body = doc.RootElement.GetProperty("body");
        Assert.Equal(JsonValueKind.Object, body.ValueKind);
    }

    public static TheoryData<GoldenEnvelope> GoldenData()
    {
        var data = new TheoryData<GoldenEnvelope>();
        foreach (var golden in GoldenEnvelopes.Set)
        {
            data.Add(golden);
        }
        return data;
    }

    private static void AssertGoldenFields(GoldenEnvelope golden, GodotEnvelope godot)
    {
        Assert.Equal(golden.Type, godot.Type);
        Assert.Equal(golden.MessageId, godot.MessageId);
        Assert.Equal(golden.SessionId, godot.SessionId);
        Assert.Equal(golden.CreatedTimeMs, godot.CreatedTimeMs);
        Assert.Equal(golden.SourceEventId, godot.SourceEventId);
        Assert.Equal(golden.AuthorityEpoch, godot.AuthorityEpoch);
        Assert.True(JsonElement.DeepEquals(Body(golden.BodyJson), godot.Body),
            $"the {golden.Name} body must match the golden body byte for byte.");
    }

    private static GodotEnvelope ToGodot(GoldenEnvelope golden) => new GodotEnvelope(
        Type: golden.Type,
        MessageId: golden.MessageId,
        SessionId: golden.SessionId,
        CreatedTimeMs: golden.CreatedTimeMs,
        Body: Body(golden.BodyJson),
        SourceEventId: golden.SourceEventId,
        AuthorityEpoch: golden.AuthorityEpoch);

    private static Envelope ToReference(GoldenEnvelope golden) => new Envelope(
        Type: golden.Type,
        MessageId: golden.MessageId,
        SessionId: golden.SessionId,
        CreatedTimeMs: golden.CreatedTimeMs,
        Body: Body(golden.BodyJson),
        SourceEventId: golden.SourceEventId,
        AuthorityEpoch: golden.AuthorityEpoch);

    private static JsonElement Body(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }
}

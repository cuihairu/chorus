using System.Text.Json;
using System.Text.Json.Serialization;

namespace Chorus.Control;

/// <summary>Preserves the legacy JSON array while allowing immutable set contracts to round-trip.</summary>
public sealed class ReadOnlyStringSetConverter : JsonConverter<IReadOnlySet<string>>
{
    public override IReadOnlySet<string> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => new HashSet<string>(JsonSerializer.Deserialize<string[]>(ref reader, options)
            ?? throw new JsonException("Expected a source receipt array."), StringComparer.Ordinal);

    public override void Write(Utf8JsonWriter writer, IReadOnlySet<string> value, JsonSerializerOptions options)
        => JsonSerializer.Serialize(writer, value.ToArray(), options);
}

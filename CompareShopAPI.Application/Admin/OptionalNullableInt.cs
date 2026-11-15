using System.Text.Json;
using System.Text.Json.Serialization;

namespace CompareShopAPI.Application.Admin;

[JsonConverter(typeof(OptionalNullableIntJsonConverter))]
public readonly record struct OptionalNullableInt(bool IsSpecified, int? Value)
{
    public static implicit operator OptionalNullableInt(int? value) => new(true, value);
}

public sealed class OptionalNullableIntJsonConverter : JsonConverter<OptionalNullableInt>
{
    public override bool HandleNull => true;

    public override OptionalNullableInt Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) return new(true, null);
        if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var value)) return new(true, value);
        throw new JsonException("Expected an integer or null for an optional integer field.");
    }

    public override void Write(Utf8JsonWriter writer, OptionalNullableInt value, JsonSerializerOptions options)
    {
        if (!value.IsSpecified || value.Value is null) writer.WriteNullValue();
        else writer.WriteNumberValue(value.Value.Value);
    }
}

using System.Text.Json;
using System.Text.Json.Serialization;

namespace NaviDnD.Data.Models;

// The AI sometimes sends a number where the format expects a string (e.g. value:4 instead of value:"4"
// for a numeric stat like "Spells prepared") — by default System.Text.Json throws a JsonException
// on that, which aborts the whole hero patch halfway (see the HeroSpell.Prepared comment
// nearby; Hero.Stats etc. default to [] as a second line of defense against the same cause).
// This converter simply accepts a number both as is and as a string.
public class LenientStringConverter : JsonConverter<string?>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType switch
        {
            JsonTokenType.String => reader.GetString(),
            JsonTokenType.Number => reader.TryGetInt64(out var l) ? l.ToString() : reader.GetDouble().ToString(),
            JsonTokenType.True or JsonTokenType.False => reader.GetBoolean().ToString(),
            JsonTokenType.Null => null,
            _ => throw new JsonException($"Unexpected token {reader.TokenType} for string value"),
        };

    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value);
}

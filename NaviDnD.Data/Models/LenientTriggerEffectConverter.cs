using System.Text.Json;
using System.Text.Json.Serialization;

namespace NaviDnD.Data.Models;

// The AI sometimes sends onExpire/onRound/spotted as a string ("onExpire": "Remove the effect") instead of an object
// {"effect": "…"} — accept it as the effect description rather than dropping the DM's whole response (the spell, the slot and the whole turn were lost).
public class LenientTriggerEffectConverter : JsonConverter<TriggerEffect?>
{
    public override TriggerEffect? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Null:
                return null;
            case JsonTokenType.String:
                return reader.GetString() is { Length: > 0 } text ? new TriggerEffect { Effect = text } : null;
            case JsonTokenType.StartObject:
                var result = new TriggerEffect();
                while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
                {
                    if (reader.TokenType != JsonTokenType.PropertyName) continue;
                    string name = reader.GetString() ?? "";
                    reader.Read();
                    if (name.Equals("effect", StringComparison.OrdinalIgnoreCase))
                        result.Effect = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
                    else if (name.Equals("once", StringComparison.OrdinalIgnoreCase))
                        result.Once = reader.TokenType switch { JsonTokenType.True => true, JsonTokenType.False => false, _ => null };
                    else
                        reader.Skip();
                }
                return result;
            default:
                reader.Skip();
                return null;
        }
    }

    public override void Write(Utf8JsonWriter writer, TriggerEffect? value, JsonSerializerOptions options)
    {
        if (value == null) { writer.WriteNullValue(); return; }
        string Name(string n) => options.PropertyNamingPolicy?.ConvertName(n) ?? n;
        writer.WriteStartObject();
        if (value.Effect != null || options.DefaultIgnoreCondition == JsonIgnoreCondition.Never) writer.WriteString(Name("Effect"), value.Effect);
        if (value.Once != null || options.DefaultIgnoreCondition == JsonIgnoreCondition.Never)
        {
            if (value.Once is bool once) writer.WriteBoolean(Name("Once"), once); else writer.WriteNull(Name("Once"));
        }
        writer.WriteEndObject();
    }
}

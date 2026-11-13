using System.Text.Json;
using System.Text.Json.Serialization;

namespace Rbm.Core;

/// <summary>Aceita "texto" (pt-BR) ou {"pt": "...", "en": "..."} no JSON.</summary>
public sealed class LocalizedTextConverter : JsonConverter<LocalizedText>
{
    public override LocalizedText? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) return null;
        if (reader.TokenType == JsonTokenType.String) return new LocalizedText(reader.GetString() ?? "");
        if (reader.TokenType != JsonTokenType.StartObject) throw new JsonException("LocalizedText inválido");
        string pt = "", en = "";
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            var prop = reader.GetString(); reader.Read();
            var val = reader.TokenType == JsonTokenType.Null ? null : reader.GetString();
            if (string.Equals(prop, "pt", StringComparison.OrdinalIgnoreCase)) pt = val ?? "";
            else if (string.Equals(prop, "en", StringComparison.OrdinalIgnoreCase)) en = val ?? "";
        }
        return new LocalizedText(pt, string.IsNullOrEmpty(en) ? null : en);
    }

    public override void Write(Utf8JsonWriter writer, LocalizedText value, JsonSerializerOptions options)
    {
        if (value.En is null) { writer.WriteStringValue(value.Pt); return; }
        writer.WriteStartObject();
        writer.WriteString("pt", value.Pt);
        writer.WriteString("en", value.En);
        writer.WriteEndObject();
    }
}

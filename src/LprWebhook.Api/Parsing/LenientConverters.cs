using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LprWebhook.Api.Parsing;

/// <summary>
/// Reads an int from a JSON number or a string ("1", " 2 ", "3.0").
/// Empty strings, nulls and anything unparseable become null instead of failing the request.
/// </summary>
public sealed class LenientInt32Converter : JsonConverter<int?>
{
    public override bool HandleNull => true;

    public override int? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Number:
                if (reader.TryGetInt32(out var i)) return i;
                return reader.TryGetDouble(out var d) ? FromDouble(d) : null;
            case JsonTokenType.String:
                var s = reader.GetString()?.Trim();
                if (string.IsNullOrEmpty(s)) return null;
                if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out i)) return i;
                return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out d) ? FromDouble(d) : null;
            default:
                reader.Skip();
                return null;
        }
    }

    public override void Write(Utf8JsonWriter writer, int? value, JsonSerializerOptions options)
    {
        if (value is null) writer.WriteNullValue();
        else writer.WriteNumberValue(value.Value);
    }

    private static int? FromDouble(double d) =>
        d == Math.Floor(d) && d is >= int.MinValue and <= int.MaxValue ? (int)d : null;
}

/// <summary>
/// Reads a string from any scalar token, so <c>"year": 2026</c> works as well as <c>"year": "2026"</c>.
/// Objects/arrays where a string is expected are kept as their raw JSON text.
/// </summary>
public sealed class LenientStringConverter : JsonConverter<string?>
{
    public override bool HandleNull => true;

    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType switch
        {
            JsonTokenType.String => reader.GetString(),
            JsonTokenType.Null => null,
            JsonTokenType.True => "true",
            JsonTokenType.False => "false",
            _ => JsonElement.ParseValue(ref reader).GetRawText(),
        };

    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options)
    {
        if (value is null) writer.WriteNullValue();
        else writer.WriteStringValue(value);
    }
}

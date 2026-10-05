using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LprWebhook.Api.Contracts;

namespace LprWebhook.Api.Parsing;

public sealed record ParseResult(PlateEventPayload? Payload, string? Error, bool Repaired = false)
{
    public bool Success => Payload is not null;
}

public static class PayloadParser
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        Converters = { new LenientStringConverter() },
    };

    public static ParseResult Parse(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return new(null, "Request body is empty.");

        PlateEventPayload? payload;
        var repaired = false;
        try
        {
            payload = JsonSerializer.Deserialize<PlateEventPayload>(body, Options);
        }
        catch (JsonException ex)
        {
            // The vendor template has unquoted placeholders ("number": ${camera_number}),
            // so an empty value produces `"number": ,` — retry once with those filled in as null.
            var fixedBody = FillEmptyValues(body);
            if (fixedBody == body || !TryDeserialize(fixedBody, out payload))
                return new(null, $"Body is not valid JSON: {ex.Message}");
            repaired = true;
        }

        if (payload is null)
            return new(null, "Body must be a JSON object.");
        if (string.IsNullOrWhiteSpace(payload.Plate?.Text))
            return new(null, "plate.text is required.");

        return new(payload, null, repaired);
    }

    private static bool TryDeserialize(string json, out PlateEventPayload? payload)
    {
        try
        {
            payload = JsonSerializer.Deserialize<PlateEventPayload>(json, Options);
            return true;
        }
        catch (JsonException)
        {
            payload = null;
            return false;
        }
    }

    /// <summary>Inserts <c>null</c> where a property has no value (<c>"a": ,</c> or <c>"a": }</c>), ignoring string contents.</summary>
    internal static string FillEmptyValues(string json)
    {
        var sb = new StringBuilder(json.Length + 16);
        var inString = false;
        var escaped = false;

        for (var i = 0; i < json.Length; i++)
        {
            var c = json[i];
            sb.Append(c);

            if (inString)
            {
                if (escaped) escaped = false;
                else if (c == '\\') escaped = true;
                else if (c == '"') inString = false;
                continue;
            }

            if (c == '"')
            {
                inString = true;
            }
            else if (c == ':')
            {
                var next = i + 1;
                while (next < json.Length && char.IsWhiteSpace(json[next])) next++;
                if (next >= json.Length || json[next] is ',' or '}')
                    sb.Append(" null");
            }
        }

        return sb.ToString();
    }
}

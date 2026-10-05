using System.Text.Json.Serialization;
using LprWebhook.Api.Parsing;

namespace LprWebhook.Api.Contracts;

// Incoming webhook body. Property names arrive in snake_case (see PayloadParser.Options).
// Everything is nullable: the vendor fills a template, so any value may be missing or empty.
public sealed record PlateEventPayload(
    string? Event,
    string? Timestamp,
    PayloadDate? Date,
    PayloadServer? Server,
    PayloadCamera? Camera,
    PayloadPlate? Plate);

public sealed record PayloadDate(
    string? Year,
    string? Month,
    string? Day,
    string? Hour,
    string? Minute,
    string? Second,
    string? Millisecond);

public sealed record PayloadServer(string? Name, string? DomainName);

public sealed record PayloadCamera(
    string? Name,
    [property: JsonConverter(typeof(LenientInt32Converter))] int? Number,
    [property: JsonConverter(typeof(LenientInt32Converter))] int? Index,
    string? Id);

public sealed record PayloadPlate(string? Text, string? Direction, string? Description, PayloadPlateList? List);

public sealed record PayloadPlateList(string? Name, string? Description);

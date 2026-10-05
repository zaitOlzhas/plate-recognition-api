using System.Security.Cryptography;
using System.Text;
using LprWebhook.Api.Contracts;
using LprWebhook.Api.Data;
using LprWebhook.Api.Parsing;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LprWebhook.Api.Endpoints;

public static class EventEndpoints
{
    private const int MaxPageSize = 500;

    public static IEndpointRouteBuilder MapEventEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/events").WithTags("Events");

        group.MapPost("/plate", ReceivePlateAsync)
            // "*/*" documents the body schema without rejecting vendors that send another Content-Type.
            .Accepts<PlateEventPayload>("application/json", "*/*")
            .Produces<ErrorResponse>(StatusCodes.Status401Unauthorized)
            .WithSummary("Webhook target for the LPR system");

        group.MapGet("", ListAsync).WithSummary("List received events, newest first");
        group.MapGet("/{id:guid}", GetAsync).WithSummary("Get one event including the raw JSON body");

        return app;
    }

    private static async Task<IResult> ReceivePlateAsync(
        HttpRequest request,
        [FromHeader(Name = "X-Api-Key")] string? apiKey,
        EventsDbContext db,
        IConfiguration config,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        if (!IsAuthorized(config["API_KEY"], apiKey))
            return TypedResults.Json(ErrorResponse.Of("Missing or invalid X-Api-Key header."), statusCode: StatusCodes.Status401Unauthorized);

        var logger = loggerFactory.CreateLogger("LprWebhook.Events");
        var rawLogger = loggerFactory.CreateLogger("LprWebhook.RawBody");

        using var reader = new StreamReader(request.Body, Encoding.UTF8);
        var body = await reader.ReadToEndAsync(ct);
        rawLogger.LogDebug("Raw webhook body ({ContentType}): {RawBody}", request.ContentType, body);

        var result = PayloadParser.Parse(body);
        if (!result.Success)
        {
            logger.LogWarning("Rejected webhook ({Error}) Raw body: {RawBody}", result.Error, body);
            return TypedResults.BadRequest(ErrorResponse.Of(result.Error!));
        }
        if (result.Repaired)
            logger.LogWarning("Webhook body had empty values and was repaired before parsing. Raw body: {RawBody}", body);

        var p = result.Payload!;
        var entity = new PlateEvent
        {
            Id = Guid.CreateVersion7(),
            ReceivedAtUtc = DateTime.UtcNow,
            CameraTime = CameraTimestamp.Resolve(p.Timestamp, p.Date),
            RawTimestamp = p.Timestamp,
            EventType = p.Event,
            PlateText = p.Plate!.Text!.Trim(),
            PlateDirection = p.Plate.Direction,
            PlateDescription = p.Plate.Description,
            PlateListName = p.Plate.List?.Name,
            PlateListDescription = p.Plate.List?.Description,
            CameraId = p.Camera?.Id,
            CameraName = p.Camera?.Name,
            CameraNumber = p.Camera?.Number,
            CameraIndex = p.Camera?.Index,
            ServerName = p.Server?.Name,
            ServerDomainName = p.Server?.DomainName,
            RawJson = body,
        };

        db.PlateEvents.Add(entity);
        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "Plate event {EventId}: plate={Plate} camera={CameraName} ({CameraId}) direction={Direction} list={ListName} cameraTime={CameraTime}",
            entity.Id, entity.PlateText, entity.CameraName, entity.CameraId, entity.PlateDirection, entity.PlateListName,
            entity.CameraTime?.ToString("yyyy-MM-ddTHH:mm:ss.fff"));

        return TypedResults.Ok(new ReceivedResponse("ok", entity.Id));
    }

    private static async Task<Ok<PagedResponse<PlateEventSummary>>> ListAsync(
        EventsDbContext db,
        string? plate,
        string? cameraId,
        DateTimeOffset? from,
        DateTimeOffset? to,
        int? page,
        int? pageSize,
        CancellationToken ct)
    {
        var pageNo = Math.Max(page ?? 1, 1);
        var size = Math.Clamp(pageSize ?? 50, 1, MaxPageSize);

        var query = db.PlateEvents.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(plate))
            query = query.Where(e => EF.Functions.Like(e.PlateText, $"%{plate.Trim()}%"));
        if (!string.IsNullOrWhiteSpace(cameraId))
            query = query.Where(e => e.CameraId == cameraId);
        if (from is { } f)
            query = query.Where(e => e.ReceivedAtUtc >= f.UtcDateTime);
        if (to is { } t)
            query = query.Where(e => e.ReceivedAtUtc <= t.UtcDateTime);

        var total = await query.CountAsync(ct);
        var items = await query
            .OrderByDescending(e => e.ReceivedAtUtc)
            .Skip((pageNo - 1) * size)
            .Take(size)
            .Select(e => new PlateEventSummary(
                e.Id, e.ReceivedAtUtc, e.CameraTime, e.EventType, e.PlateText, e.PlateDirection,
                e.PlateListName, e.CameraId, e.CameraName, e.CameraNumber, e.ServerName))
            .ToListAsync(ct);

        return TypedResults.Ok(new PagedResponse<PlateEventSummary>(pageNo, size, total, items));
    }

    private static async Task<Results<Ok<PlateEventDetails>, NotFound>> GetAsync(Guid id, EventsDbContext db, CancellationToken ct)
    {
        var e = await db.PlateEvents.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (e is null) return TypedResults.NotFound();

        return TypedResults.Ok(new PlateEventDetails(
            e.Id, e.ReceivedAtUtc, e.CameraTime, e.RawTimestamp, e.EventType, e.PlateText, e.PlateDirection,
            e.PlateDescription, e.PlateListName, e.PlateListDescription, e.CameraId, e.CameraName,
            e.CameraNumber, e.CameraIndex, e.ServerName, e.ServerDomainName, e.RawJson));
    }

    private static bool IsAuthorized(string? expected, string? provided)
    {
        if (string.IsNullOrEmpty(expected)) return true;
        if (string.IsNullOrEmpty(provided)) return false;
        // Compare hashes so the check is constant-time regardless of key length.
        return CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(expected)),
            SHA256.HashData(Encoding.UTF8.GetBytes(provided)));
    }
}

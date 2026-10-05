namespace LprWebhook.Api.Contracts;

public sealed record ReceivedResponse(string Status, Guid Id);

public sealed record ErrorResponse(string Status, string Error)
{
    public static ErrorResponse Of(string error) => new("error", error);
}

public sealed record PagedResponse<T>(int Page, int PageSize, int Total, IReadOnlyList<T> Items);

public sealed record PlateEventSummary(
    Guid Id,
    DateTime ReceivedAtUtc,
    DateTime? CameraTime,
    string? Event,
    string PlateText,
    string? Direction,
    string? ListName,
    string? CameraId,
    string? CameraName,
    int? CameraNumber,
    string? ServerName);

public sealed record PlateEventDetails(
    Guid Id,
    DateTime ReceivedAtUtc,
    DateTime? CameraTime,
    string? RawTimestamp,
    string? Event,
    string PlateText,
    string? Direction,
    string? PlateDescription,
    string? ListName,
    string? ListDescription,
    string? CameraId,
    string? CameraName,
    int? CameraNumber,
    int? CameraIndex,
    string? ServerName,
    string? ServerDomainName,
    string RawJson);

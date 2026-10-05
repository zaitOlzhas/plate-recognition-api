namespace LprWebhook.Api.Data;

public class PlateEvent
{
    public Guid Id { get; set; }

    /// <summary>When this API received the webhook (UTC).</summary>
    public DateTime ReceivedAtUtc { get; set; }

    /// <summary>Camera's local wall-clock time as reported by the vendor (no timezone).</summary>
    public DateTime? CameraTime { get; set; }

    public string? RawTimestamp { get; set; }
    public string? EventType { get; set; }

    public string PlateText { get; set; } = "";
    public string? PlateDirection { get; set; }
    public string? PlateDescription { get; set; }
    public string? PlateListName { get; set; }
    public string? PlateListDescription { get; set; }

    public string? CameraId { get; set; }
    public string? CameraName { get; set; }
    public int? CameraNumber { get; set; }
    public int? CameraIndex { get; set; }

    public string? ServerName { get; set; }
    public string? ServerDomainName { get; set; }

    public string RawJson { get; set; } = "";
}

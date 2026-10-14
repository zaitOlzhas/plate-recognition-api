namespace LprWebhook.Api.Data;

/// <summary>
/// What someone decided about a plate event with the Telegram alert buttons. A separate table (not columns on
/// plate_events) so existing databases get it without migrations; the primary key makes the first press win.
/// </summary>
public class PlateDecision
{
    public Guid EventId { get; set; }

    /// <summary>"granted" (gate opened) or "denied".</summary>
    public string Status { get; set; } = "";

    public long DecidedByUserId { get; set; }
    public string? DecidedByName { get; set; }
    public DateTime DecidedAtUtc { get; set; }
}

public static class PlateDecisionStatus
{
    public const string Granted = "granted";
    public const string Denied = "denied";
}

/// <summary>
/// One alert message sent for a plate event (several per chat if it was resent), so every copy can be updated once
/// someone decides.
/// </summary>
public class PlateAlertMessage
{
    public Guid EventId { get; set; }
    public long ChatId { get; set; }
    public int MessageId { get; set; }
    public DateTime SentAtUtc { get; set; }
}

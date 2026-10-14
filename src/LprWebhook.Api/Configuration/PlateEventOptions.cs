namespace LprWebhook.Api.Configuration;

/// <summary>Bound from the "PlateEvents" config section.</summary>
public class PlateEventOptions
{
    /// <summary>
    /// The camera sends the same plate several times per second while a car is in view. A plate seen again by the
    /// same camera within this many seconds of its last sighting is dropped (not stored, not logged, no Telegram
    /// alert), and each repeat restarts the window. 0 turns de-duplication off.
    /// </summary>
    public int DuplicateWindowSeconds { get; set; } = 30;
}

using System.Threading.Channels;
using LprWebhook.Api.Data;

namespace LprWebhook.Api.Services;

/// <summary>What the Telegram alert for a stored plate event shows.</summary>
/// <param name="ResentBy">Set when an admin sent the alert again from the web viewer; shown in the message.</param>
/// <param name="PreviousDecision">On a resend of a decided event, the decision it replaced (e.g. "denied by @bob").</param>
public sealed record PlateAlert(
    Guid EventId, string Plate, string? Camera, DateTime? CameraTime, DateTime ReceivedAtUtc, string? ResentBy = null,
    string? PreviousDecision = null)
{
    public static PlateAlert From(PlateEvent e, string? resentBy = null, string? previousDecision = null)
        => new(e.Id, e.PlateText, e.CameraName ?? e.CameraId, e.CameraTime, e.ReceivedAtUtc, resentBy, previousDecision);
}

/// <summary>
/// Hands new plate events from the webhook to TelegramBotService, so the webhook answers without waiting for
/// Telegram. Registered only when the bot is enabled. Bounded: if Telegram is unreachable for a long time the
/// oldest alerts are dropped instead of piling up.
/// </summary>
public sealed class PlateAlertQueue
{
    private readonly Channel<PlateAlert> _channel = Channel.CreateBounded<PlateAlert>(
        new BoundedChannelOptions(100) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });

    public void Enqueue(PlateAlert alert) => _channel.Writer.TryWrite(alert);

    public ChannelReader<PlateAlert> Reader => _channel.Reader;
}

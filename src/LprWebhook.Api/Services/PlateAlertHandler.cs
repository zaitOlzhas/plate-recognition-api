using LprWebhook.Api.Access;
using LprWebhook.Api.Data;
using Microsoft.EntityFrameworkCore;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace LprWebhook.Api.Services;

public sealed record PlateDecisionResult(string Outcome, string Message, PlateEvent? Event = null, int? RelayStatusCode = null)
{
    public bool IsOk => Outcome == "success";
}

/// <summary>
/// Telegram alerts for new plate events. Every known user gets one; only those who may use /open_gate get the
/// "Open gate" / "Deny" buttons. The first press decides for everybody and every copy of the alert (with or without
/// buttons) is updated to show who decided.
/// </summary>
public class PlateAlertHandler(
    EventsDbContext db,
    AccessService access,
    IRelayService relay,
    ILogger<PlateAlertHandler> logger)
{
    public const string OpenAction = "open";
    public const string DenyAction = "deny";

    public async Task SendAsync(ITelegramBotClient bot, PlateAlert alert, CancellationToken ct)
    {
        var recipients = await access.ListUsersWithPermissionAsync(BotPermissions.OpenGate, ct);
        if (recipients.Count == 0)
        {
            logger.LogWarning("The bot knows no users, so no Telegram alert was sent for plate event {EventId}", alert.EventId);
            return;
        }

        // Callback data is limited to 64 bytes: "open:" + 32 hex digits fits.
        var id = alert.EventId.ToString("N");
        var keyboard = new InlineKeyboardMarkup(new[]
        {
            new[]
            {
                InlineKeyboardButton.WithCallbackData("Открыть шлагбаум", $"{OpenAction}:{id}"),
                InlineKeyboardButton.WithCallbackData("Отклонить", $"{DenyAction}:{id}"),
            },
        });
        var text = Format(alert);
        var entities = PlateBold(alert);

        var sent = 0;
        foreach (var (chatId, canOpenGate) in recipients)
        {
            try
            {
                // A private chat's ID is the user's ID. Users without open_gate see the event but get no buttons.
                var message = await bot.SendMessage(chatId, text, entities: entities, replyMarkup: canOpenGate ? keyboard : null, cancellationToken: ct);
                db.PlateAlertMessages.Add(new PlateAlertMessage
                {
                    EventId = alert.EventId, ChatId = chatId, MessageId = message.MessageId, SentAtUtc = DateTime.UtcNow,
                });
                sent++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Usually someone who was granted access by ID but never started the bot, or who blocked it.
                logger.LogWarning(ex, "Could not send the alert for plate event {EventId} to {ChatId}", alert.EventId, chatId);
            }
        }

        await db.SaveChangesAsync(CancellationToken.None);
        logger.LogInformation("Plate alert {EventId} ({Plate}) sent to {Sent} of {Total} recipients ({WithButtons} may open the gate)",
            alert.EventId, alert.Plate, sent, recipients.Count, recipients.Count(r => r.Allowed));
    }

    /// <summary>A press on an alert button. Sets the outcome on <paramref name="record"/>; TelegramBotService saves it.</summary>
    public async Task HandleCallbackAsync(ITelegramBotClient bot, CallbackQuery query, BotCommandLog record, CancellationToken ct)
    {
        if (!TryParse(query.Data, out var action, out var eventId))
        {
            record.ResultMessage = "Неизвестная кнопка";
            await bot.AnswerCallbackQuery(query.Id, "Неизвестная кнопка.", cancellationToken: ct);
            return; // stays "ignored"
        }

        record.Command = action == OpenAction ? "/open_gate" : "/deny";
        var actor = new Actor(query.From.Id, query.From.Username is { } un ? "@" + un : record.UserFullName);
        var status = action == OpenAction ? PlateDecisionStatus.Granted : PlateDecisionStatus.Denied;

        var result = await DecideAsync(eventId, status, actor, ct);
        record.Outcome = result.Outcome;
        record.ResultMessage = result.Message;
        record.RelayStatusCode = result.RelayStatusCode;

        await bot.AnswerCallbackQuery(query.Id, result.Message, showAlert: !result.IsOk, cancellationToken: ct);
        if (result.Event is { Decision: not null } decided)
            await UpdateAlertsAsync(bot, decided, query.Message, ct);
    }

    /// <summary>
    /// Records the decision. "granted" also pulses the relay; if that fails the decision is undone so someone can
    /// try again. The first decision wins: later presses get "Already ...".
    /// </summary>
    public async Task<PlateDecisionResult> DecideAsync(Guid eventId, string status, Actor actor, CancellationToken ct)
    {
        if (!await access.CanUseAsync(actor.UserId, BotPermissions.OpenGate, ct))
        {
            logger.LogWarning("Denied plate decision {Status} for {EventId} by {UserId}", status, eventId, actor.UserId);
            return new("denied", $"Нет доступа. Ваш Telegram ID: {actor.UserId} — отправьте его администратору, чтобы он выдал доступ.");
        }

        var ev = await db.PlateEvents.Include(e => e.Decision).FirstOrDefaultAsync(e => e.Id == eventId, ct);
        if (ev is null)
            return new("failed", "Это событие больше не существует.");
        if (ev.Decision is not null)
            return new("failed", AlreadyDecided(ev.Decision), ev);

        var decision = new PlateDecision
        {
            EventId = eventId, Status = status, DecidedByUserId = actor.UserId, DecidedByName = actor.Name,
            DecidedAtUtc = DateTime.UtcNow,
        };
        ev.Decision = decision;
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Someone else pressed a button at the same moment and their row got in first.
            db.ChangeTracker.Clear();
            ev = await db.PlateEvents.AsNoTracking().Include(e => e.Decision).FirstAsync(e => e.Id == eventId, ct);
            return new("failed", ev.Decision is null ? "Попробуйте ещё раз." : AlreadyDecided(ev.Decision), ev);
        }

        if (status == PlateDecisionStatus.Denied)
        {
            logger.LogInformation("Plate event {EventId} ({Plate}) denied by {Actor}", eventId, ev.PlateText, actor.Name);
            return new("success", $"Denied {ev.PlateText}.", ev);
        }

        var relayResult = await relay.TriggerRelayAsync(ct);
        int? relayStatus = relayResult.StatusCode == 0 ? null : relayResult.StatusCode;
        if (!relayResult.Success)
        {
            logger.LogWarning("Failed to open gate for plate event {EventId} ({Plate}): {Error}", eventId, ev.PlateText, relayResult.Message);
            db.PlateDecisions.Remove(decision);
            await db.SaveChangesAsync(CancellationToken.None);
            return new("failed", $"Не удалось открыть шлагбаум: {relayResult.Message}", null, relayStatus);
        }

        logger.LogInformation("Gate opened for plate event {EventId} ({Plate}) by {Actor}", eventId, ev.PlateText, actor.Name);
        return new("success", $"Gate opened for {ev.PlateText}.", ev, relayStatus);
    }

    /// <summary>Replaces the buttons on every copy of the alert with who decided.</summary>
    private async Task UpdateAlertsAsync(ITelegramBotClient bot, PlateEvent ev, Message? pressed, CancellationToken ct)
    {
        var targets = await db.PlateAlertMessages.AsNoTracking()
            .Where(m => m.EventId == ev.Id)
            .Select(m => new { m.ChatId, m.MessageId })
            .ToListAsync(ct);
        if (pressed is not null && !targets.Any(t => t.ChatId == pressed.Chat.Id && t.MessageId == pressed.MessageId))
            targets.Add(new { ChatId = pressed.Chat.Id, pressed.MessageId });

        var d = ev.Decision!;
        var alert = PlateAlert.From(ev);
        var text = $"{Format(alert)}\n\n" +
                   (d.Status == PlateDecisionStatus.Granted ? "✅ Gate opened by " : "⛔ Denied by ") +
                   (d.DecidedByName ?? d.DecidedByUserId.ToString());

        foreach (var t in targets)
        {
            try
            {
                // No replyMarkup: removes the buttons.
                await bot.EditMessageText(t.ChatId, t.MessageId, text, entities: PlateBold(alert), cancellationToken: ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // E.g. the user deleted the message, or it already shows this text.
                logger.LogDebug(ex, "Could not update alert message {MessageId} in chat {ChatId}", t.MessageId, t.ChatId);
            }
        }
    }

    /// <summary>Plate on the first line (made bold by <see cref="PlateBold"/>), then the time, then the camera.</summary>
    public static string Format(PlateAlert a)
    {
        var time = a.CameraTime is { } t
            ? t.ToString("yyyy-MM-dd HH:mm:ss")
            : a.ReceivedAtUtc.ToString("yyyy-MM-dd HH:mm:ss") + " UTC";
        var text = $"{a.Plate}\n{time}\nCamera: {a.Camera ?? "unknown"}";
        return a.ResentBy is null ? text : $"{text}\n\nResent by {a.ResentBy}";
    }

    /// <summary>
    /// Bolds the plate at the start of <see cref="Format"/>'s text. An entity instead of a parse mode, so plate or
    /// camera names never need escaping. Telegram has no font sizes; bold is as prominent as text gets.
    /// </summary>
    public static MessageEntity[] PlateBold(PlateAlert a)
        => [new MessageEntity { Type = MessageEntityType.Bold, Offset = 0, Length = a.Plate.Length }];

    private static string AlreadyDecided(PlateDecision d)
        => $"{(d.Status == PlateDecisionStatus.Granted ? "Уже открыто" : "Уже отклонено")} пользователем {d.DecidedByName ?? d.DecidedByUserId.ToString()}.";

    /// <summary>"open:&lt;guid&gt;" or "deny:&lt;guid&gt;".</summary>
    public static bool TryParse(string? data, out string action, out Guid eventId)
    {
        action = "";
        eventId = Guid.Empty;
        var parts = data?.Split(':', 2);
        if (parts is not [var a, var id] || a is not (OpenAction or DenyAction) || !Guid.TryParse(id, out eventId))
            return false;
        action = a;
        return true;
    }
}

namespace LprWebhook.Api.Data;

/// <summary>One text message received by the Telegram bot and what the bot did with it.</summary>
public class BotCommandLog
{
    public Guid Id { get; set; }

    /// <summary>When this API received the message (UTC).</summary>
    public DateTime ReceivedAtUtc { get; set; }

    /// <summary>When Telegram says the user sent the message (UTC).</summary>
    public DateTime SentAtUtc { get; set; }

    public long ChatId { get; set; }
    public string? ChatType { get; set; }
    public long? UserId { get; set; }
    public string? Username { get; set; }
    public string? UserFullName { get; set; }
    public int MessageId { get; set; }

    /// <summary>Full message text as sent.</summary>
    public string Text { get; set; } = "";

    /// <summary>Lower-cased command without the "@botname" suffix (e.g. "/open_gate"), or null for plain text.</summary>
    public string? Command { get; set; }

    /// <summary>"success", "failed" (relay said no), "error" (exception), or "ignored" (not a known command).</summary>
    public string Outcome { get; set; } = "";

    /// <summary>Relay result or error text.</summary>
    public string? ResultMessage { get; set; }

    public int? RelayStatusCode { get; set; }
    public int DurationMs { get; set; }
}

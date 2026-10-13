namespace LprWebhook.Api.Configuration;

public class TelegramBotOptions
{
    public bool Enabled { get; set; }
    public string BotToken { get; set; } = string.Empty;

    /// <summary>Telegram user ID of the bot owner: always an admin, can promote/demote admins, can't be removed.</summary>
    public long? OwnerUserId { get; set; }

    /// <summary>
    /// Address of this API as seen from the admin's browser (e.g. http://192.168.1.50:5080), used to build the
    /// /weblogin link. If empty, the bot sends the bare code to paste into the Access tab instead.
    /// </summary>
    public string WebBaseUrl { get; set; } = string.Empty;

    public RelayOptions Relay { get; set; } = new();
}

public class RelayOptions
{
    public string BaseUrl { get; set; } = string.Empty;
    public string Endpoint { get; set; } = string.Empty;

    public string GetFullUrl() => $"{BaseUrl.TrimEnd('/')}/{Endpoint.TrimStart('/')}";
}

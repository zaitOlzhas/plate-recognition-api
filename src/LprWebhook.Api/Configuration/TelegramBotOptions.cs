namespace LprWebhook.Api.Configuration;

public class TelegramBotOptions
{
    public bool Enabled { get; set; }
    public string BotToken { get; set; } = string.Empty;
    public RelayOptions Relay { get; set; } = new();
}

public class RelayOptions
{
    public string BaseUrl { get; set; } = string.Empty;
    public string Endpoint { get; set; } = string.Empty;

    public string GetFullUrl() => $"{BaseUrl.TrimEnd('/')}/{Endpoint.TrimStart('/')}";
}

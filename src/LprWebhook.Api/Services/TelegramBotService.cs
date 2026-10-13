using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using LprWebhook.Api.Configuration;
using Microsoft.Extensions.Options;

namespace LprWebhook.Api.Services;

public class TelegramBotService : IHostedService
{
    private readonly ILogger<TelegramBotService> _logger;
    private readonly TelegramBotOptions _options;
    private readonly IServiceProvider _serviceProvider;
    private ITelegramBotClient? _botClient;
    private CancellationTokenSource? _cts;

    public TelegramBotService(
        IOptions<TelegramBotOptions> options,
        IServiceProvider serviceProvider,
        ILogger<TelegramBotService> logger)
    {
        _options = options.Value;
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("Telegram bot is disabled");
            return Task.CompletedTask;
        }

        if (string.IsNullOrWhiteSpace(_options.BotToken))
        {
            _logger.LogWarning("Telegram bot is enabled but BotToken is not configured");
            return Task.CompletedTask;
        }

        _logger.LogInformation("Starting Telegram bot service");

        _botClient = new TelegramBotClient(_options.BotToken);
        _cts = new CancellationTokenSource();

        var receiverOptions = new ReceiverOptions
        {
            AllowedUpdates = new[] { UpdateType.Message },
            DropPendingUpdates = true
        };

        _botClient.StartReceiving(
            HandleUpdateAsync,
            HandleErrorAsync,
            receiverOptions,
            _cts.Token);

        _logger.LogInformation("Telegram bot started successfully");
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_cts != null)
        {
            _logger.LogInformation("Stopping Telegram bot service");
            await _cts.CancelAsync();
            _cts.Dispose();
        }
    }

    private async Task HandleUpdateAsync(
        ITelegramBotClient botClient,
        Update update,
        CancellationToken ct)
    {
        if (update.Message is not { } message)
            return;

        if (message.Text is not { } messageText)
            return;

        var chatId = message.Chat.Id;
        var username = message.From?.Username ?? "unknown";

        _logger.LogInformation(
            "Received message from {Username} (ChatId={ChatId}): {Message}",
            username, chatId, messageText);

        if (messageText.Trim().ToLowerInvariant() == "/open_gate")
        {
            await HandleOpenGateCommandAsync(botClient, chatId, username, ct);
        }
    }

    private async Task HandleOpenGateCommandAsync(
        ITelegramBotClient botClient,
        long chatId,
        string username,
        CancellationToken ct)
    {
        _logger.LogInformation(
            "Processing /open_gate command from {Username} (ChatId={ChatId})",
            username, chatId);

        // Create a scope to resolve scoped services
        using var scope = _serviceProvider.CreateScope();
        var relayService = scope.ServiceProvider.GetRequiredService<IRelayService>();

        try
        {
            // Send "typing" indicator
            await botClient.SendChatAction(chatId, ChatAction.Typing, cancellationToken: ct);

            var result = await relayService.TriggerRelayAsync(ct);

            string responseMessage;
            if (result.Success)
            {
                responseMessage = $"Gate opened successfully!\n\n{result.Message}";
                _logger.LogInformation(
                    "Gate opened successfully for {Username}",
                    username);
            }
            else
            {
                responseMessage = $"Failed to open gate\n\n{result.Message}";
                _logger.LogWarning(
                    "Failed to open gate for {Username}: {Error}",
                    username, result.Message);
            }

            await botClient.SendMessage(
                chatId,
                responseMessage,
                cancellationToken: ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling /open_gate command");

            try
            {
                await botClient.SendMessage(
                    chatId,
                    "An unexpected error occurred. Please try again later.",
                    cancellationToken: ct);
            }
            catch
            {
                // Ignore errors when sending error message
            }
        }
    }

    private Task HandleErrorAsync(
        ITelegramBotClient botClient,
        Exception exception,
        CancellationToken ct)
    {
        _logger.LogError(exception, "Telegram bot error occurred");
        return Task.CompletedTask;
    }
}

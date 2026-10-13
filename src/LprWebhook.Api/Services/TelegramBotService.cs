using System.Diagnostics;
using LprWebhook.Api.Access;
using LprWebhook.Api.Data;
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

        if (_options.OwnerUserId is not > 0)
        {
            _logger.LogWarning(
                "TelegramBot:OwnerUserId is not set: nobody is an admin, so nobody can run commands or grant access. " +
                "Set TELEGRAM_OWNER_ID to your Telegram user ID (send /help to the bot to see it)");
        }

        _ = SetCommandMenuAsync(_botClient, _cts.Token);

        _logger.LogInformation("Telegram bot started successfully");
        return Task.CompletedTask;
    }

    /// <summary>The "/" menu in Telegram clients. Admin commands are listed by /help for admins only.</summary>
    private async Task SetCommandMenuAsync(ITelegramBotClient botClient, CancellationToken ct)
    {
        try
        {
            BotCommand[] commands =
            [
                .. BotPermissions.All.Select(p => new BotCommand { Command = p.Key, Description = p.Description }),
                new BotCommand { Command = "help", Description = "Your ID, role and available commands" },
            ];
            await botClient.SetMyCommands(commands, cancellationToken: ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not set the Telegram command menu");
        }
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
        var stopwatch = Stopwatch.StartNew();

        _logger.LogInformation(
            "Received message from {Username} (ChatId={ChatId}): {Message}",
            username, chatId, messageText);

        var record = new BotCommandLog
        {
            Id = Guid.CreateVersion7(),
            ReceivedAtUtc = DateTime.UtcNow,
            SentAtUtc = DateTime.SpecifyKind(message.Date, DateTimeKind.Utc),
            ChatId = chatId,
            ChatType = message.Chat.Type.ToString(),
            UserId = message.From?.Id,
            Username = message.From?.Username,
            UserFullName = $"{message.From?.FirstName} {message.From?.LastName}".Trim() is { Length: > 0 } name ? name : null,
            MessageId = message.MessageId,
            Text = messageText,
            Command = ParseCommand(messageText),
            Outcome = "ignored",
        };

        // Create a scope to resolve scoped services
        using var scope = _serviceProvider.CreateScope();

        try
        {
            if (message.From is { } from)
            {
                await scope.ServiceProvider.GetRequiredService<AccessService>()
                    .TouchUserAsync(from.Id, from.Username, record.UserFullName, ct);
            }

            await scope.ServiceProvider.GetRequiredService<BotCommandHandler>()
                .HandleAsync(botClient, message, record, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling {Command} from {Username}", record.Command, username);
            // Keep the outcome if the command already ran and only the Telegram reply failed.
            if (record.Outcome == "ignored")
                record.Outcome = "error";
            record.ResultMessage = record.ResultMessage is null ? ex.Message : $"{record.ResultMessage} (reply failed: {ex.Message})";

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

        record.DurationMs = (int)stopwatch.ElapsedMilliseconds;
        await SaveCommandAsync(scope.ServiceProvider, record);
    }

    /// <summary>"/Open_Gate@MyBot now" -> "/open_gate"; null if the text isn't a command.</summary>
    private static string? ParseCommand(string text)
    {
        var trimmed = text.Trim();
        if (!trimmed.StartsWith('/'))
            return null;

        var token = trimmed.Split(' ', 2)[0];
        var at = token.IndexOf('@');
        return (at > 0 ? token[..at] : token).ToLowerInvariant();
    }

    private async Task SaveCommandAsync(IServiceProvider services, BotCommandLog record)
    {
        try
        {
            var db = services.GetRequiredService<EventsDbContext>();
            db.BotCommands.Add(record);
            // Not tied to the bot's token: a command that already ran should still be recorded during shutdown.
            await db.SaveChangesAsync(CancellationToken.None);

            _logger.LogInformation(
                "Bot command {CommandId}: user={Username} ({UserId}) chat={ChatId} command={Command} outcome={Outcome} durationMs={DurationMs}",
                record.Id, record.Username, record.UserId, record.ChatId, record.Command, record.Outcome, record.DurationMs);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to store bot command from {Username} (ChatId={ChatId}): {Text}",
                record.Username, record.ChatId, record.Text);
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

using System.Text;
using LprWebhook.Api.Access;
using LprWebhook.Api.Configuration;
using LprWebhook.Api.Data;
using Microsoft.Extensions.Options;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace LprWebhook.Api.Services;

/// <summary>
/// Runs one bot command for one message. Sets <see cref="BotCommandLog.Outcome"/> and
/// <see cref="BotCommandLog.ResultMessage"/> on the record; TelegramBotService saves it.
/// Replies are plain text (no parse mode) so usernames with "_" etc. never break formatting.
/// </summary>
public class BotCommandHandler(
    AccessService access,
    IRelayService relay,
    LoginCodeStore loginCodes,
    IOptions<TelegramBotOptions> options,
    ILogger<BotCommandHandler> logger)
{
    private const int TelegramMaxMessageLength = 4096;

    public async Task HandleAsync(ITelegramBotClient bot, Message message, BotCommandLog record, CancellationToken ct)
    {
        var args = message.Text!.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).ToArray();
        var userId = record.UserId ?? 0;
        var actor = new Actor(userId, record.Username is { } un ? "@" + un : record.UserFullName);

        switch (record.Command)
        {
            case null:
                return; // plain text: logged as "ignored", no reply
            case "/start":
            case "/help":
                await HelpAsync(bot, record, userId, ct);
                break;
            case "/open_gate":
                await OpenGateAsync(bot, record, userId, ct);
                break;
            case "/users":
                await UsersAsync(bot, record, userId, ct);
                break;
            case "/grant":
            case "/revoke":
                await ChangePermissionAsync(bot, record, actor, args, grant: record.Command == "/grant", ct);
                break;
            case "/promote":
            case "/demote":
                await ChangeRoleAsync(bot, record, actor, args, promote: record.Command == "/promote", ct);
                break;
            case "/weblogin":
                await WebLoginAsync(bot, message, record, userId, ct);
                break;
            default:
                await ReplyAsync(bot, record, "Неизвестная команда. Отправьте /help, чтобы увидеть доступные команды.", ct);
                break; // stays "ignored"
        }
    }

    private async Task HelpAsync(ITelegramBotClient bot, BotCommandLog record, long userId, CancellationToken ct)
    {
        var role = await access.GetRoleAsync(userId, ct);
        var usable = await access.GetUsablePermissionsAsync(userId, ct);

        var sb = new StringBuilder();
        sb.AppendLine($"Your Telegram ID: {userId}");
        sb.AppendLine($"Role: {role}");
        sb.AppendLine();
        if (usable.Count == 0)
            sb.AppendLine("You can't run any commands yet. Send your Telegram ID to an admin to get access.");
        else
        {
            sb.AppendLine("Commands you can use:");
            foreach (var p in usable) sb.AppendLine($"{p.Command} - {p.Description}");
        }

        if (Roles.IsAdmin(role))
        {
            sb.AppendLine();
            sb.AppendLine("Admin commands:");
            sb.AppendLine("/users - list users and their permissions");
            sb.AppendLine("/grant <@user|id> <permission> - allow a command");
            sb.AppendLine("/revoke <@user|id> <permission> - remove a command");
            sb.AppendLine("/weblogin - get a one-time link to the web Access page");
            sb.AppendLine("/promote <@user|id> - make an admin");
            sb.AppendLine("/demote <@user|id> - remove admin");
            sb.AppendLine($"Permissions: {string.Join(", ", BotPermissions.All.Select(p => p.Key))}");
        }

        record.Outcome = "success";
        await ReplyAsync(bot, record, sb.ToString(), ct);
    }

    private async Task OpenGateAsync(ITelegramBotClient bot, BotCommandLog record, long userId, CancellationToken ct)
    {
        if (!await access.CanUseAsync(userId, BotPermissions.OpenGate, ct))
        {
            await DenyAsync(bot, record, $"Нет доступа. Ваш Telegram ID: {userId} — отправьте его администратору, чтобы он выдал доступ.", ct);
            return;
        }

        logger.LogInformation("Processing /open_gate command from {Username} (UserId={UserId})", record.Username, userId);
        await bot.SendChatAction(record.ChatId, ChatAction.Typing, cancellationToken: ct);

        var result = await relay.TriggerRelayAsync(ct);
        record.Outcome = result.Success ? "success" : "failed";
        record.ResultMessage = result.Message;
        record.RelayStatusCode = result.StatusCode == 0 ? null : result.StatusCode;

        if (result.Success)
            logger.LogInformation("Gate opened successfully for {Username}", record.Username);
        else
            logger.LogWarning("Failed to open gate for {Username}: {Error}", record.Username, result.Message);

        await bot.SendMessage(record.ChatId,
            result.Success ? "Шлагбаум успешно открыт" :$"Не удалось открыть шлагбаум\n\n{result.Message}",
            cancellationToken: ct);
    }

    private async Task UsersAsync(ITelegramBotClient bot, BotCommandLog record, long userId, CancellationToken ct)
    {
        if (!await access.IsAdminAsync(userId, ct))
        {
            await DenyAsync(bot, record, "Только администраторы могут просматривать список пользователей.", ct);
            return;
        }

        var users = await access.ListUsersAsync(ct);
        var sb = new StringBuilder($"Users ({users.Count}):\n");
        foreach (var u in users)
        {
            var name = u.Username is { Length: > 0 } un ? "@" + un : u.FullName ?? "(unknown)";
            var perms = Roles.IsAdmin(u.Role) ? "all commands" : u.Permissions.Count == 0 ? "no access" : string.Join(", ", u.Permissions);
            sb.Append($"\n{name} - {u.UserId}\n  {u.Role}: {perms}\n");
        }

        record.Outcome = "success";
        record.ResultMessage = $"{users.Count} users";
        await ReplyAsync(bot, record, sb.ToString(), ct);
    }

    private async Task ChangePermissionAsync(
        ITelegramBotClient bot, BotCommandLog record, Actor actor, string[] args, bool grant, CancellationToken ct)
    {
        if (!await access.IsAdminAsync(actor.UserId, ct))
        {
            await DenyAsync(bot, record, "Только администраторы могут изменять права.", ct);
            return;
        }
        if (args.Length != 2)
        {
            await InvalidAsync(bot, record, $"Использование: {record.Command} <@user|id> <право>\nПрава: {string.Join(", ", BotPermissions.All.Select(p => p.Key))}", ct);
            return;
        }
        if (await access.ResolveUserIdAsync(args[0], ct) is not { } targetId)
        {
            await InvalidAsync(bot, record, UnknownUserMessage(args[0]), ct);
            return;
        }

        var result = grant
            ? await access.GrantAsync(actor, targetId, args[1], "telegram", ct)
            : await access.RevokeAsync(actor, targetId, args[1], "telegram", ct);
        await ReplyWithResultAsync(bot, record, result, ct);
    }

    private async Task ChangeRoleAsync(
        ITelegramBotClient bot, BotCommandLog record, Actor actor, string[] args, bool promote, CancellationToken ct)
    {
        if (!await access.IsAdminAsync(actor.UserId, ct))
        {
            await DenyAsync(bot, record, "Только администраторы могут назначать и снимать администраторов.", ct);
            return;
        }
        if (args.Length != 1)
        {
            await InvalidAsync(bot, record, $"Использование: {record.Command} <@user|id>", ct);
            return;
        }
        if (await access.ResolveUserIdAsync(args[0], ct) is not { } targetId)
        {
            await InvalidAsync(bot, record, UnknownUserMessage(args[0]), ct);
            return;
        }

        var result = await access.SetRoleAsync(actor, targetId, promote ? Roles.Admin : Roles.User, "telegram", ct);
        await ReplyWithResultAsync(bot, record, result, ct);
    }

    private async Task WebLoginAsync(ITelegramBotClient bot, Message message, BotCommandLog record, long userId, CancellationToken ct)
    {
        if (!await access.IsAdminAsync(userId, ct))
        {
            await DenyAsync(bot, record, "Только администраторы могут входить на веб-страницу Access.", ct);
            return;
        }
        // Anyone in a group chat could use a link posted there.
        if (message.Chat.Type != ChatType.Private)
        {
            await InvalidAsync(bot, record, "Отправьте /weblogin в личном чате с ботом, а не в группе.", ct);
            return;
        }

        var code = loginCodes.Create(userId);
        var minutes = (int)LoginCodeStore.Lifetime.TotalMinutes;
        var baseUrl = options.Value.WebBaseUrl.Trim().TrimEnd('/');
        var text = baseUrl.Length > 0
            ? $"Open this link within {minutes} minutes to sign in (works once):\n{baseUrl}/login?code={Uri.EscapeDataString(code)}"
            : $"Paste this code into the Access tab within {minutes} minutes (works once):\n{code}";

        record.Outcome = "success";
        record.ResultMessage = "login code issued"; // never store the code itself
        await ReplyAsync(bot, record, text, ct);
    }

    private static string UnknownUserMessage(string token)
        => $"Пользователь {token} неизвестен. Укажите его числовой Telegram ID или попросите его сначала написать боту, чтобы бот узнал его @username.";

    private async Task ReplyWithResultAsync(ITelegramBotClient bot, BotCommandLog record, AccessResult result, CancellationToken ct)
    {
        record.Outcome = result.Status switch
        {
            AccessStatus.Ok => "success",
            AccessStatus.Denied => "denied",
            _ => "failed",
        };
        record.ResultMessage = result.Message;
        await bot.SendMessage(record.ChatId, result.Message, cancellationToken: ct);
    }

    private async Task DenyAsync(ITelegramBotClient bot, BotCommandLog record, string text, CancellationToken ct)
    {
        logger.LogWarning("Denied {Command} for {Username} (UserId={UserId})", record.Command, record.Username, record.UserId);
        record.Outcome = "denied";
        record.ResultMessage = text;
        await bot.SendMessage(record.ChatId, text, cancellationToken: ct);
    }

    private async Task InvalidAsync(ITelegramBotClient bot, BotCommandLog record, string text, CancellationToken ct)
    {
        record.Outcome = "failed";
        record.ResultMessage = text;
        await bot.SendMessage(record.ChatId, text, cancellationToken: ct);
    }

    private static Task ReplyAsync(ITelegramBotClient bot, BotCommandLog record, string text, CancellationToken ct)
    {
        if (text.Length > TelegramMaxMessageLength)
            text = text[..(TelegramMaxMessageLength - 20)] + "\n… (truncated)";
        return bot.SendMessage(record.ChatId, text, cancellationToken: ct);
    }
}

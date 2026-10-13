using LprWebhook.Api.Contracts;
using LprWebhook.Api.Data;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace LprWebhook.Api.Endpoints;

public static class BotCommandEndpoints
{
    private const int MaxPageSize = 500;

    public static IEndpointRouteBuilder MapBotCommandEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/bot-commands", ListAsync)
            .WithTags("Telegram bot")
            .WithSummary("List messages received by the Telegram bot, newest first");

        return app;
    }

    private static async Task<Ok<PagedResponse<BotCommandLogItem>>> ListAsync(
        EventsDbContext db,
        string? user,
        long? chatId,
        string? command,
        string? outcome,
        DateTimeOffset? from,
        DateTimeOffset? to,
        int? page,
        int? pageSize,
        CancellationToken ct)
    {
        var pageNo = Math.Max(page ?? 1, 1);
        var size = Math.Clamp(pageSize ?? 50, 1, MaxPageSize);

        var query = db.BotCommands.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(user))
        {
            var u = user.Trim().TrimStart('@');
            query = long.TryParse(u, out var userId)
                ? query.Where(c => c.UserId == userId)
                : query.Where(c => EF.Functions.Like(c.Username, $"%{u}%") || EF.Functions.Like(c.UserFullName, $"%{u}%"));
        }
        if (chatId is { } chat)
            query = query.Where(c => c.ChatId == chat);
        if (!string.IsNullOrWhiteSpace(command))
        {
            var cmd = "/" + command.Trim().TrimStart('/').ToLowerInvariant();
            query = query.Where(c => c.Command == cmd);
        }
        if (!string.IsNullOrWhiteSpace(outcome))
            query = query.Where(c => c.Outcome == outcome.Trim().ToLowerInvariant());
        if (from is { } f)
            query = query.Where(c => c.ReceivedAtUtc >= f.UtcDateTime);
        if (to is { } t)
            query = query.Where(c => c.ReceivedAtUtc <= t.UtcDateTime);

        var total = await query.CountAsync(ct);
        var items = await query
            .OrderByDescending(c => c.ReceivedAtUtc)
            .Skip((pageNo - 1) * size)
            .Take(size)
            .Select(c => new BotCommandLogItem(
                c.Id, c.ReceivedAtUtc, c.SentAtUtc, c.ChatId, c.ChatType, c.UserId, c.Username, c.UserFullName,
                c.MessageId, c.Text, c.Command, c.Outcome, c.ResultMessage, c.RelayStatusCode, c.DurationMs))
            .ToListAsync(ct);

        return TypedResults.Ok(new PagedResponse<BotCommandLogItem>(pageNo, size, total, items));
    }
}

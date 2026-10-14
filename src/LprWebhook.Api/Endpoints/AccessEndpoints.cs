using LprWebhook.Api.Access;
using LprWebhook.Api.Contracts;
using LprWebhook.Api.Data;
using LprWebhook.Api.Services;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LprWebhook.Api.Endpoints;

/// <summary>Admin API behind the web Access tab. Every request needs the /weblogin cookie and a live admin role.</summary>
public static class AccessEndpoints
{
    private const int MaxPageSize = 500;
    private const string ActorKey = "access.actor";

    public static IEndpointRouteBuilder MapAccessEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/access")
            .WithTags("Access")
            .RequireAuthorization()
            .AddEndpointFilter(RequireAdminAsync);

        group.MapGet("/permissions", () => TypedResults.Ok(BotPermissions.All)).WithSummary("Commands that can be granted");
        group.MapGet("/users", ListUsersAsync).WithSummary("Known users with roles and permissions");
        group.MapPost("/users", AddUserAsync).WithSummary("Add a user by Telegram ID");
        group.MapPut("/users/{userId:long}/permissions/{permission}", GrantAsync).WithSummary("Allow a command");
        group.MapDelete("/users/{userId:long}/permissions/{permission}", RevokeAsync).WithSummary("Remove a command");
        group.MapPut("/users/{userId:long}/role", SetRoleAsync).WithSummary("Promote/demote (owner only)");
        group.MapGet("/audit", ListAuditAsync).WithSummary("Permission changes, newest first");
        group.MapPost("/events/{id:guid}/resend-alert", ResendAlertAsync).WithSummary("Send a plate event's Telegram alert again (clears an earlier decision)");
        group.MapPost("/open-gate", OpenGateAsync).WithSummary("Pulse the gate relay, like /open_gate in the bot");

        return app;
    }

    // A demoted admin's cookie stays valid until it expires, so the role is re-checked on every request.
    private static async ValueTask<object?> RequireAdminAsync(EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
    {
        var http = ctx.HttpContext;
        var access = http.RequestServices.GetRequiredService<AccessService>();
        if (AuthEndpoints.GetUserId(http.User) is not { } userId || !await access.IsAdminAsync(userId, http.RequestAborted))
            return TypedResults.Json(ErrorResponse.Of("Admins only. Sign in again with /weblogin."), statusCode: StatusCodes.Status403Forbidden);

        http.Items[ActorKey] = new Actor(userId, await access.DescribeAsync(userId, http.RequestAborted));
        return await next(ctx);
    }

    private static Actor GetActor(HttpContext http) => (Actor)http.Items[ActorKey]!;

    private static async Task<Ok<IReadOnlyList<UserAccessItem>>> ListUsersAsync(AccessService access, CancellationToken ct)
        => TypedResults.Ok(await access.ListUsersAsync(ct));

    private static async Task<IResult> AddUserAsync(AddUserRequest request, HttpContext http, AccessService access, CancellationToken ct)
        => ToResult(await access.AddUserAsync(GetActor(http), request.UserId, "web", ct));

    private static async Task<IResult> GrantAsync(long userId, string permission, HttpContext http, AccessService access, CancellationToken ct)
        => ToResult(await access.GrantAsync(GetActor(http), userId, permission, "web", ct));

    private static async Task<IResult> RevokeAsync(long userId, string permission, HttpContext http, AccessService access, CancellationToken ct)
        => ToResult(await access.RevokeAsync(GetActor(http), userId, permission, "web", ct));

    private static async Task<IResult> SetRoleAsync(long userId, SetRoleRequest request, HttpContext http, AccessService access, CancellationToken ct)
        => ToResult(await access.SetRoleAsync(GetActor(http), userId, request.Role?.Trim().ToLowerInvariant() ?? "", "web", ct));

    private static async Task<Ok<PagedResponse<PermissionAuditItem>>> ListAuditAsync(
        int? page, int? pageSize, AccessService access, CancellationToken ct)
        => TypedResults.Ok(await access.ListAuditAsync(Math.Max(page ?? 1, 1), Math.Clamp(pageSize ?? 50, 1, MaxPageSize), ct));

    /// <summary>
    /// Queues another copy of the alert (with fresh buttons) to every known user. Works for decided events too: the
    /// earlier decision is cleared so the event can be decided again, and the new message says what it was.
    /// Whichever button is pressed first then decides and updates every copy, old and new.
    /// </summary>
    private static async Task<IResult> ResendAlertAsync(
        Guid id, HttpContext http, EventsDbContext db, [FromServices] PlateAlertQueue? alerts,
        ILoggerFactory loggerFactory, CancellationToken ct)
    {
        if (alerts is null)
            return TypedResults.BadRequest(new AccessActionResponse("invalid", "The Telegram bot is disabled, so there is nothing to send."));

        var ev = await db.PlateEvents.Include(e => e.Decision).FirstOrDefaultAsync(e => e.Id == id, ct);
        if (ev is null)
            return TypedResults.NotFound(new AccessActionResponse("invalid", "Event not found."));

        var actor = GetActor(http);
        var logger = loggerFactory.CreateLogger("LprWebhook.Events");
        string? previous = null;
        if (ev.Decision is { } d)
        {
            previous = PlateAlertHandler.Describe(d);
            db.PlateDecisions.Remove(d);
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Decision on plate event {EventId} ({Plate}) cleared by {Actor} for a resend; it was {Previous}",
                ev.Id, ev.PlateText, actor.Name, previous);
        }

        alerts.Enqueue(PlateAlert.From(ev, actor.Name, previous));
        logger.LogInformation("Plate alert {EventId} ({Plate}) resent by {Actor}", ev.Id, ev.PlateText, actor.Name);
        return TypedResults.Ok(new AccessActionResponse("ok", previous is null
            ? "Alert sent again to every known bot user."
            : $"Alert sent again. The earlier decision ({previous}) was cleared, so it can be decided again."));
    }

    /// <summary>
    /// The web version of /open_gate. Logged to bot_commands with chat type "web" so the Bot commands tab shows it
    /// next to the Telegram presses.
    /// </summary>
    private static async Task<IResult> OpenGateAsync(
        HttpContext http, EventsDbContext db, [FromServices] IRelayService? relay, ILoggerFactory loggerFactory, CancellationToken ct)
    {
        if (relay is null)
            return TypedResults.BadRequest(new AccessActionResponse("invalid", "The Telegram bot is disabled, so the relay is not configured."));

        var actor = GetActor(http);
        var logger = loggerFactory.CreateLogger("LprWebhook.Gate");
        var started = DateTime.UtcNow;
        var result = await relay.TriggerRelayAsync(ct);

        if (result.Success)
            logger.LogInformation("Gate opened from the web by {Actor}", actor.Name);
        else
            logger.LogWarning("Failed to open gate from the web for {Actor}: {Error}", actor.Name, result.Message);

        try
        {
            var user = await db.BotUsers.AsNoTracking().FirstOrDefaultAsync(u => u.UserId == actor.UserId, CancellationToken.None);
            db.BotCommands.Add(new BotCommandLog
            {
                Id = Guid.NewGuid(), ReceivedAtUtc = started, SentAtUtc = started, ChatType = "web",
                UserId = actor.UserId, Username = user?.Username, UserFullName = user?.FullName ?? actor.Name,
                Text = "/open_gate", Command = "/open_gate", Outcome = result.Success ? "success" : "failed",
                ResultMessage = result.Message, RelayStatusCode = result.StatusCode == 0 ? null : result.StatusCode,
                DurationMs = (int)(DateTime.UtcNow - started).TotalMilliseconds,
            });
            await db.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            // The gate has already been pulsed; a failed log write must not turn that into an error.
            logger.LogError(ex, "Could not log the web /open_gate by {Actor}", actor.Name);
        }

        return result.Success
            ? TypedResults.Ok(new AccessActionResponse("ok", $"Gate opened. {result.Message}"))
            : TypedResults.Json(new AccessActionResponse("failed", $"Failed to open gate: {result.Message}"), statusCode: StatusCodes.Status502BadGateway);
    }

    private static IResult ToResult(AccessResult result) => result.Status switch
    {
        AccessStatus.Ok => TypedResults.Ok(new AccessActionResponse("ok", result.Message)),
        AccessStatus.Denied => TypedResults.Json(new AccessActionResponse("denied", result.Message), statusCode: StatusCodes.Status403Forbidden),
        _ => TypedResults.BadRequest(new AccessActionResponse("invalid", result.Message)),
    };
}

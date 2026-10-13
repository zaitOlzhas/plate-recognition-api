using LprWebhook.Api.Access;
using LprWebhook.Api.Contracts;
using Microsoft.AspNetCore.Http.HttpResults;

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

    private static IResult ToResult(AccessResult result) => result.Status switch
    {
        AccessStatus.Ok => TypedResults.Ok(new AccessActionResponse("ok", result.Message)),
        AccessStatus.Denied => TypedResults.Json(new AccessActionResponse("denied", result.Message), statusCode: StatusCodes.Status403Forbidden),
        _ => TypedResults.BadRequest(new AccessActionResponse("invalid", result.Message)),
    };
}

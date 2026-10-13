using System.Security.Claims;
using LprWebhook.Api.Access;
using LprWebhook.Api.Contracts;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http.HttpResults;

namespace LprWebhook.Api.Endpoints;

/// <summary>
/// Web sign-in for admins. There are no passwords: an admin sends /weblogin to the bot, which proves who they are,
/// and gets a single-use code that is exchanged here for a cookie.
/// </summary>
public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        // The link the bot sends. A browser navigation, so it redirects instead of returning JSON.
        app.MapGet("/login", LoginFromLinkAsync).ExcludeFromDescription();

        var group = app.MapGroup("/api/auth").WithTags("Auth");
        group.MapPost("/login", LoginAsync).WithSummary("Exchange a /weblogin code for a session cookie");
        group.MapPost("/logout", LogoutAsync).WithSummary("End the session");
        group.MapGet("/me", MeAsync).WithSummary("The signed-in admin, or 401");

        return app;
    }

    private static async Task<RedirectHttpResult> LoginFromLinkAsync(
        string? code, HttpContext http, LoginCodeStore codes, AccessService access, CancellationToken ct)
    {
        var me = await SignInAsync(code, http, codes, access, ct);
        return TypedResults.Redirect(me is null ? "/?login=failed#access" : "/#access");
    }

    private static async Task<Results<Ok<MeResponse>, UnauthorizedHttpResult>> LoginAsync(
        LoginRequest request, HttpContext http, LoginCodeStore codes, AccessService access, CancellationToken ct)
    {
        var me = await SignInAsync(request.Code, http, codes, access, ct);
        return me is null ? TypedResults.Unauthorized() : TypedResults.Ok(me);
    }

    private static async Task<Ok<AccessActionResponse>> LogoutAsync(HttpContext http)
    {
        await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return TypedResults.Ok(new AccessActionResponse("ok", "Signed out."));
    }

    private static async Task<Results<Ok<MeResponse>, UnauthorizedHttpResult>> MeAsync(
        HttpContext http, AccessService access, CancellationToken ct)
    {
        if (GetUserId(http.User) is not { } userId)
            return TypedResults.Unauthorized();

        // The cookie only says who you are; whether you're still an admin is checked live.
        var role = await access.GetRoleAsync(userId, ct);
        if (!Roles.IsAdmin(role))
        {
            await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return TypedResults.Unauthorized();
        }
        return TypedResults.Ok(new MeResponse(userId, await access.DescribeAsync(userId, ct), role));
    }

    private static async Task<MeResponse?> SignInAsync(
        string? code, HttpContext http, LoginCodeStore codes, AccessService access, CancellationToken ct)
    {
        if (codes.Redeem(code) is not { } userId) return null;

        var role = await access.GetRoleAsync(userId, ct);
        if (!Roles.IsAdmin(role)) return null; // demoted between /weblogin and opening the link

        var name = await access.DescribeAsync(userId, ct);
        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, userId.ToString()), new Claim(ClaimTypes.Name, name)],
            CookieAuthenticationDefaults.AuthenticationScheme);
        await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
        return new MeResponse(userId, name, role);
    }

    public static long? GetUserId(ClaimsPrincipal user)
        => long.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;
}

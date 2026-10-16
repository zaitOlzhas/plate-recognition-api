using System.Net;
using System.Net.Http.Json;
using LprWebhook.Api.Access;
using LprWebhook.Api.Contracts;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace LprWebhook.Api.Tests;

/// <summary>
/// The web sign-in and admin API end to end, on a throwaway database. Login codes are taken from
/// LoginCodeStore directly, standing in for the bot's /weblogin reply.
/// </summary>
public sealed class AccessApiTests : IDisposable
{
    private const long OwnerId = 1001;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lpr-tests-" + Guid.NewGuid().ToString("N"));
    private readonly WebApplicationFactory<Program> _factory;

    public AccessApiTests()
    {
        Directory.CreateDirectory(_dir);
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b
            .UseSetting("ConnectionStrings:Default", $"Data Source={Path.Combine(_dir, "test.db")}")
            .UseSetting("TelegramBot:Enabled", "false")
            .UseSetting("TelegramBot:OwnerUserId", OwnerId.ToString()));
    }

    public void Dispose()
    {
        _factory.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* best effort */ }
    }

    private HttpClient NewClient() => _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    private string IssueCode(long userId) => _factory.Services.GetRequiredService<LoginCodeStore>().Create(userId);

    private async Task<HttpClient> SignedInClientAsync(long userId)
    {
        var client = NewClient();
        var response = await client.GetAsync($"/login?code={Uri.EscapeDataString(IssueCode(userId))}");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/#access", response.Headers.Location?.OriginalString);
        return client;
    }

    [Fact]
    public async Task Admin_api_requires_a_session()
    {
        var client = NewClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/access/users")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PutAsync("/api/access/users/5/permissions/open_gate", null)).StatusCode);
    }

    [Fact]
    public async Task Login_link_signs_the_owner_in_and_works_only_once()
    {
        var code = IssueCode(OwnerId);
        var client = NewClient();

        var first = await client.GetAsync($"/login?code={Uri.EscapeDataString(code)}");
        Assert.Equal("/#access", first.Headers.Location?.OriginalString);

        var me = await client.GetFromJsonAsync<MeResponse>("/api/auth/me");
        Assert.Equal((OwnerId, Roles.Owner), (me!.UserId, me.Role));

        var reused = await NewClient().GetAsync($"/login?code={Uri.EscapeDataString(code)}");
        Assert.Equal("/?login=failed#access", reused.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task Non_admin_cannot_sign_in_even_with_a_code()
    {
        var client = NewClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(IssueCode(2002)));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Owner_grants_and_revokes_and_every_change_is_audited_as_web()
    {
        var client = await SignedInClientAsync(OwnerId);

        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/access/users", new AddUserRequest(3003))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsync("/api/access/users/3003/permissions/open_gate", null)).StatusCode);

        var users = await client.GetFromJsonAsync<List<UserAccessItem>>("/api/access/users");
        Assert.Equal([BotPermissions.OpenGate], users!.Single(u => u.UserId == 3003).Permissions);

        Assert.Equal(HttpStatusCode.OK, (await client.DeleteAsync("/api/access/users/3003/permissions/open_gate")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsync("/api/access/users/3003/permissions/nope", null)).StatusCode);

        var audit = await client.GetFromJsonAsync<PagedResponse<PermissionAuditItem>>("/api/access/audit");
        Assert.Equal(4, audit!.Total);
        Assert.All(audit.Items, a => Assert.Equal(("web", OwnerId), (a.Source, a.ActorUserId)));
        Assert.Equal(["invalid", "ok", "ok", "ok"], audit.Items.Select(a => a.Result));
    }

    [Fact]
    public async Task Admin_can_promote_and_loses_access_immediately_when_demoted()
    {
        var owner = await SignedInClientAsync(OwnerId);
        await owner.PutAsJsonAsync("/api/access/users/4004/role", new SetRoleRequest("admin"));

        var admin = await SignedInClientAsync(4004);
        var promote = await admin.PutAsJsonAsync("/api/access/users/5005/role", new SetRoleRequest("admin"));
        Assert.Equal(HttpStatusCode.OK, promote.StatusCode);

        var demoteOwner = await admin.PutAsJsonAsync($"/api/access/users/{OwnerId}/role", new SetRoleRequest("user"));
        Assert.Equal(HttpStatusCode.BadRequest, demoteOwner.StatusCode);

        await owner.PutAsJsonAsync("/api/access/users/4004/role", new SetRoleRequest("user"));
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.GetAsync("/api/access/users")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await admin.GetAsync("/api/auth/me")).StatusCode);
    }

    [Fact]
    public async Task Logout_ends_the_session()
    {
        var client = await SignedInClientAsync(OwnerId);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/api/auth/logout", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me")).StatusCode);
    }
}

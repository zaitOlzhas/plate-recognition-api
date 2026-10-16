using LprWebhook.Api.Access;
using LprWebhook.Api.Configuration;
using LprWebhook.Api.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace LprWebhook.Api.Tests;

public sealed class AccessServiceTests : IDisposable
{
    private const long OwnerId = 1;
    private const long AdminId = 2;
    private const long UserId = 3;
    private const long OtherAdminId = 4;

    private static readonly Actor Owner = new(OwnerId, "@owner");
    private static readonly Actor Admin = new(AdminId, "@admin");
    private static readonly Actor User = new(UserId, "@user");

    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly EventsDbContext _db;
    private readonly AccessService _access;

    public AccessServiceTests()
    {
        _connection.Open();
        _db = new EventsDbContext(new DbContextOptionsBuilder<EventsDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();
        _access = new AccessService(_db, Options.Create(new TelegramBotOptions { OwnerUserId = OwnerId }));

        _db.BotUsers.AddRange(
            new BotUser { UserId = AdminId, Username = "admin", Role = Roles.Admin },
            new BotUser { UserId = UserId, Username = "user" },
            new BotUser { UserId = OtherAdminId, Username = "admin2", Role = Roles.Admin });
        _db.SaveChanges();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private Task<int> AuditCountAsync() => _db.PermissionAudit.CountAsync();

    [Fact]
    public async Task Owner_comes_from_config_and_can_use_everything_without_a_db_row()
    {
        Assert.Equal(Roles.Owner, await _access.GetRoleAsync(OwnerId));
        Assert.True(await _access.CanUseAsync(OwnerId, BotPermissions.OpenGate));
    }

    [Fact]
    public async Task Admins_can_use_every_command()
        => Assert.True(await _access.CanUseAsync(AdminId, BotPermissions.OpenGate));

    [Fact]
    public async Task User_without_a_grant_is_denied_and_unknown_users_too()
    {
        Assert.False(await _access.CanUseAsync(UserId, BotPermissions.OpenGate));
        Assert.False(await _access.CanUseAsync(999, BotPermissions.OpenGate));
    }

    [Fact]
    public async Task Grant_allows_the_command_and_revoke_takes_it_away()
    {
        var granted = await _access.GrantAsync(Admin, UserId, "/Open_Gate", "web");
        Assert.True(granted.IsOk);
        Assert.True(await _access.CanUseAsync(UserId, BotPermissions.OpenGate));

        var revoked = await _access.RevokeAsync(Admin, UserId, "open_gate", "telegram");
        Assert.True(revoked.IsOk);
        Assert.False(await _access.CanUseAsync(UserId, BotPermissions.OpenGate));

        var audit = await _db.PermissionAudit.OrderBy(a => a.AtUtc).ToListAsync();
        Assert.Collection(audit,
            a => { Assert.Equal(("grant", "ok", "web", AdminId, (long?)UserId), (a.Action, a.Result, a.Source, a.ActorUserId, a.TargetUserId)); },
            a => { Assert.Equal(("revoke", "ok", "telegram"), (a.Action, a.Result, a.Source)); });
    }

    [Fact]
    public async Task Granting_to_an_unseen_id_creates_the_user()
    {
        var result = await _access.GrantAsync(Admin, 555, BotPermissions.OpenGate, "web");

        Assert.True(result.IsOk);
        var created = await _db.BotUsers.FindAsync(555L);
        Assert.NotNull(created);
        Assert.Equal(AdminId, created.AddedByUserId);
        Assert.True(await _access.CanUseAsync(555, BotPermissions.OpenGate));
    }

    [Fact]
    public async Task Regular_user_cannot_grant_and_the_attempt_is_audited()
    {
        var result = await _access.GrantAsync(User, UserId, BotPermissions.OpenGate, "telegram");

        Assert.Equal(AccessStatus.Denied, result.Status);
        Assert.False(await _access.CanUseAsync(UserId, BotPermissions.OpenGate));
        var audit = Assert.Single(await _db.PermissionAudit.ToListAsync());
        Assert.Equal(("grant", "denied", UserId), (audit.Action, audit.Result, audit.ActorUserId));
    }

    [Fact]
    public async Task Unknown_permission_is_invalid()
    {
        var result = await _access.GrantAsync(Admin, UserId, "launch_rockets", "web");

        Assert.Equal(AccessStatus.Invalid, result.Status);
        Assert.Equal(1, await AuditCountAsync());
    }

    [Fact]
    public async Task Admins_and_the_owner_can_promote_and_demote_but_users_cannot()
    {
        Assert.Equal(AccessStatus.Denied, (await _access.SetRoleAsync(User, UserId, Roles.Admin, "web")).Status);
        Assert.Equal(AccessStatus.Denied, (await _access.SetRoleAsync(User, OtherAdminId, Roles.User, "web")).Status);
        Assert.Equal(Roles.User, await _access.GetRoleAsync(UserId));
        Assert.Equal(Roles.Admin, await _access.GetRoleAsync(OtherAdminId));

        Assert.True((await _access.SetRoleAsync(Admin, UserId, Roles.Admin, "telegram")).IsOk);
        Assert.Equal(Roles.Admin, await _access.GetRoleAsync(UserId));

        Assert.True((await _access.SetRoleAsync(Admin, OtherAdminId, Roles.User, "telegram")).IsOk);
        Assert.Equal(Roles.User, await _access.GetRoleAsync(OtherAdminId));

        Assert.True((await _access.SetRoleAsync(Owner, UserId, Roles.User, "telegram")).IsOk);
        Assert.Equal(Roles.User, await _access.GetRoleAsync(UserId));

        Assert.Equal(5, await AuditCountAsync());
    }

    [Fact]
    public async Task Admin_cannot_demote_the_owner()
    {
        var result = await _access.SetRoleAsync(Admin, OwnerId, Roles.User, "web");

        Assert.Equal(AccessStatus.Invalid, result.Status);
        Assert.Equal(Roles.Owner, await _access.GetRoleAsync(OwnerId));
    }

    [Fact]
    public async Task Owner_cannot_be_demoted()
    {
        var result = await _access.SetRoleAsync(Owner, OwnerId, Roles.User, "web");

        Assert.Equal(AccessStatus.Invalid, result.Status);
        Assert.Equal(Roles.Owner, await _access.GetRoleAsync(OwnerId));
    }

    [Fact]
    public async Task Admin_cannot_change_another_admins_permissions()
    {
        var result = await _access.RevokeAsync(Admin, OtherAdminId, BotPermissions.OpenGate, "web");

        Assert.False(result.IsOk);
        Assert.True(await _access.CanUseAsync(OtherAdminId, BotPermissions.OpenGate));
    }

    [Fact]
    public async Task Without_an_owner_configured_nobody_is_admin()
    {
        var access = new AccessService(_db, Options.Create(new TelegramBotOptions()));
        Assert.Equal(Roles.User, await access.GetRoleAsync(OwnerId));
        Assert.False(await access.CanUseAsync(OwnerId, BotPermissions.OpenGate));
    }

    [Theory]
    [InlineData("3", UserId)]
    [InlineData("@user", UserId)]
    [InlineData("USER", UserId)]
    [InlineData("777", 777L)]
    public async Task ResolveUserId_accepts_ids_and_known_usernames(string token, long expected)
        => Assert.Equal(expected, await _access.ResolveUserIdAsync(token));

    [Fact]
    public async Task ResolveUserId_returns_null_for_unknown_username()
        => Assert.Null(await _access.ResolveUserIdAsync("@stranger"));

    [Fact]
    public async Task ListUsers_includes_the_owner_first_and_admins_have_all_permissions()
    {
        await _access.GrantAsync(Admin, UserId, BotPermissions.OpenGate, "web");
        var users = await _access.ListUsersAsync();

        Assert.Equal(OwnerId, users[0].UserId);
        Assert.Equal(Roles.Owner, users[0].Role);
        Assert.Contains(BotPermissions.OpenGate, users.Single(u => u.UserId == AdminId).Permissions);
        Assert.Equal([BotPermissions.OpenGate], users.Single(u => u.UserId == UserId).Permissions);
    }
}

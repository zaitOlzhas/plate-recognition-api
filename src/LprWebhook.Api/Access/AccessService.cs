using LprWebhook.Api.Configuration;
using LprWebhook.Api.Contracts;
using LprWebhook.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace LprWebhook.Api.Access;

/// <summary>Who is making a change, for the audit trail.</summary>
public sealed record Actor(long UserId, string? Name);

public enum AccessStatus { Ok, Denied, Invalid }

public sealed record AccessResult(AccessStatus Status, string Message)
{
    public bool IsOk => Status == AccessStatus.Ok;
    public static AccessResult Ok(string message) => new(AccessStatus.Ok, message);
    public static AccessResult Denied(string message) => new(AccessStatus.Denied, message);
    public static AccessResult Invalid(string message) => new(AccessStatus.Invalid, message);
}

/// <summary>
/// Roles, command permissions and the audit trail. Every rule lives here so the Telegram bot and the web
/// Access tab behave identically. Nothing is cached: each check reads the database, so a revoke is immediate.
/// </summary>
public class AccessService(EventsDbContext db, IOptions<TelegramBotOptions> options)
{
    private readonly long? _ownerId = options.Value.OwnerUserId is > 0 ? options.Value.OwnerUserId : null;

    public bool IsOwner(long userId) => _ownerId == userId;

    public async Task<string> GetRoleAsync(long userId, CancellationToken ct = default)
    {
        if (IsOwner(userId)) return Roles.Owner;
        var role = await db.BotUsers.Where(u => u.UserId == userId).Select(u => u.Role).FirstOrDefaultAsync(ct);
        return role == Roles.Admin ? Roles.Admin : Roles.User;
    }

    public async Task<bool> IsAdminAsync(long userId, CancellationToken ct = default)
        => Roles.IsAdmin(await GetRoleAsync(userId, ct));

    public async Task<bool> CanUseAsync(long userId, string permission, CancellationToken ct = default)
    {
        if (await IsAdminAsync(userId, ct)) return true;
        return await db.UserPermissions.AnyAsync(p => p.UserId == userId && p.Permission == permission, ct);
    }

    /// <summary>
    /// Every known user (the owner and everyone in bot_users), each flagged with whether they may use
    /// <paramref name="permission"/>. Used for plate alerts: everyone sees them, only allowed users get buttons.
    /// </summary>
    public async Task<IReadOnlyList<(long UserId, bool Allowed)>> ListUsersWithPermissionAsync(string permission, CancellationToken ct = default)
    {
        var users = await db.BotUsers.Select(u => new { u.UserId, u.Role }).ToListAsync(ct);
        var granted = (await db.UserPermissions.Where(p => p.Permission == permission).Select(p => p.UserId).ToListAsync(ct)).ToHashSet();

        var result = new List<(long, bool)>();
        if (_ownerId is { } ownerId) result.Add((ownerId, true));
        result.AddRange(users
            .Where(u => !IsOwner(u.UserId))
            .Select(u => (u.UserId, u.Role == Roles.Admin || granted.Contains(u.UserId))));
        return result;
    }

    /// <summary>Permissions the user can actually use (all of them for admins).</summary>
    public async Task<IReadOnlyList<BotPermission>> GetUsablePermissionsAsync(long userId, CancellationToken ct = default)
    {
        if (await IsAdminAsync(userId, ct)) return BotPermissions.All;
        var granted = await db.UserPermissions.Where(p => p.UserId == userId).Select(p => p.Permission).ToListAsync(ct);
        return BotPermissions.All.Where(p => granted.Contains(p.Key)).ToList();
    }

    /// <summary>Records that a user messaged the bot and refreshes their name, so admins can find them by @username.</summary>
    public async Task TouchUserAsync(long userId, string? username, string? fullName, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var user = await db.BotUsers.FindAsync([userId], ct);
        if (user is null)
        {
            user = new BotUser { UserId = userId, CreatedAtUtc = now };
            db.BotUsers.Add(user);
        }
        user.Username = username;
        user.FullName = fullName;
        user.LastSeenAtUtc = now;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>"12345" -> 12345; "@bob" or "bob" -> the ID of a user who has messaged the bot; otherwise null.</summary>
    public async Task<long?> ResolveUserIdAsync(string? token, CancellationToken ct = default)
    {
        var t = token?.Trim();
        if (string.IsNullOrEmpty(t)) return null;
        if (long.TryParse(t, out var id) && id > 0) return id;

        var name = t.TrimStart('@').ToLower();
        return await db.BotUsers
            .Where(u => u.Username != null && u.Username.ToLower() == name)
            .Select(u => (long?)u.UserId)
            .FirstOrDefaultAsync(ct);
    }

    public Task<AccessResult> GrantAsync(Actor actor, long targetId, string permission, string source, CancellationToken ct = default)
        => ChangePermissionAsync(actor, targetId, permission, grant: true, source, ct);

    public Task<AccessResult> RevokeAsync(Actor actor, long targetId, string permission, string source, CancellationToken ct = default)
        => ChangePermissionAsync(actor, targetId, permission, grant: false, source, ct);

    private async Task<AccessResult> ChangePermissionAsync(
        Actor actor, long targetId, string permission, bool grant, string source, CancellationToken ct)
    {
        var action = grant ? "grant" : "revoke";
        var perm = BotPermissions.Find(permission);
        var target = await db.BotUsers.FindAsync([targetId], ct);
        var targetName = Describe(target, targetId);

        AccessResult result;
        if (!await IsAdminAsync(actor.UserId, ct))
            result = AccessResult.Denied("Только администраторы могут изменять права.");
        else if (perm is null)
            result = AccessResult.Invalid(
                $"Неизвестное право \"{permission}\". Доступные: {string.Join(", ", BotPermissions.All.Select(p => p.Key))}.");
        else if (await IsAdminAsync(targetId, ct))
            result = AccessResult.Invalid($"{targetName} — администратор и уже может использовать все команды.");
        else
        {
            target ??= AddUserRow(targetId, actor.UserId);
            var existing = await db.UserPermissions.FindAsync([targetId, perm.Key], ct);

            if (grant && existing is null)
            {
                db.UserPermissions.Add(new UserPermission
                {
                    UserId = targetId, Permission = perm.Key, GrantedByUserId = actor.UserId, GrantedAtUtc = DateTime.UtcNow,
                });
                result = AccessResult.Ok($"{targetName} can now use {perm.Command}.");
            }
            else if (!grant && existing is not null)
            {
                db.UserPermissions.Remove(existing);
                result = AccessResult.Ok($"{targetName} can no longer use {perm.Command}.");
            }
            else
            {
                result = AccessResult.Ok(grant
                    ? $"{targetName} already has {perm.Command}. No change."
                    : $"{targetName} didn't have {perm.Command}. No change.");
            }
        }

        AddAudit(actor, targetId, targetName, action, perm?.Key ?? permission, source, result);
        await db.SaveChangesAsync(ct);
        return result;
    }

    /// <summary>Promote to admin or demote to user. Any admin may do it; the owner's role can't change.</summary>
    public async Task<AccessResult> SetRoleAsync(Actor actor, long targetId, string role, string source, CancellationToken ct = default)
    {
        var target = await db.BotUsers.FindAsync([targetId], ct);
        var targetName = Describe(target, targetId);
        var action = role == Roles.Admin ? "promote" : "demote";

        AccessResult result;
        if (!await IsAdminAsync(actor.UserId, ct))
            result = AccessResult.Denied("Только администраторы могут назначать и снимать администраторов.");
        else if (role is not (Roles.Admin or Roles.User))
            result = AccessResult.Invalid($"Неизвестная роль \"{role}\". Используйте \"admin\" или \"user\".");
        else if (IsOwner(targetId))
            result = AccessResult.Invalid("Роль владельца изменить нельзя.");
        else
        {
            target ??= AddUserRow(targetId, actor.UserId);
            if (target.Role == role)
                result = AccessResult.Ok($"{targetName} is already {(role == Roles.Admin ? "an admin" : "a regular user")}. No change.");
            else
            {
                target.Role = role;
                result = AccessResult.Ok(role == Roles.Admin
                    ? $"{targetName} is now an admin."
                    : $"{targetName} is no longer an admin. Their individual command grants still apply.");
            }
        }

        AddAudit(actor, targetId, targetName, action, null, source, result);
        await db.SaveChangesAsync(ct);
        return result;
    }

    /// <summary>Adds a user by Telegram ID before they've messaged the bot. Admins only.</summary>
    public async Task<AccessResult> AddUserAsync(Actor actor, long targetId, string source, CancellationToken ct = default)
    {
        var target = await db.BotUsers.FindAsync([targetId], ct);
        var targetName = Describe(target, targetId);

        AccessResult result;
        if (!await IsAdminAsync(actor.UserId, ct))
            result = AccessResult.Denied("Только администраторы могут добавлять пользователей.");
        else if (targetId <= 0)
            result = AccessResult.Invalid("Telegram ID пользователя должен быть положительным числом.");
        else if (target is not null || IsOwner(targetId))
            result = AccessResult.Ok($"{targetName} is already known. No change.");
        else
        {
            AddUserRow(targetId, actor.UserId);
            result = AccessResult.Ok($"Added user {targetId}. Grant them commands next.");
        }

        AddAudit(actor, targetId, targetName, "add_user", null, source, result);
        await db.SaveChangesAsync(ct);
        return result;
    }

    public async Task<IReadOnlyList<UserAccessItem>> ListUsersAsync(CancellationToken ct = default)
    {
        var users = await db.BotUsers.AsNoTracking().ToListAsync(ct);
        var grants = (await db.UserPermissions.AsNoTracking().ToListAsync(ct))
            .ToLookup(p => p.UserId, p => p.Permission);

        if (_ownerId is { } ownerId && users.All(u => u.UserId != ownerId))
            users.Add(new BotUser { UserId = ownerId });

        return users
            .Select(u =>
            {
                var role = IsOwner(u.UserId) ? Roles.Owner : u.Role == Roles.Admin ? Roles.Admin : Roles.User;
                var permissions = Roles.IsAdmin(role)
                    ? BotPermissions.All.Select(p => p.Key).ToList()
                    : grants[u.UserId].Where(k => BotPermissions.Find(k) is not null).ToList();
                return new UserAccessItem(u.UserId, u.Username, u.FullName, role, permissions,
                    u.CreatedAtUtc == default ? null : u.CreatedAtUtc, u.LastSeenAtUtc);
            })
            .OrderBy(u => u.Role switch { Roles.Owner => 0, Roles.Admin => 1, _ => 2 })
            .ThenByDescending(u => u.LastSeenAtUtc)
            .ToList();
    }

    public async Task<PagedResponse<PermissionAuditItem>> ListAuditAsync(int page, int pageSize, CancellationToken ct = default)
    {
        var query = db.PermissionAudit.AsNoTracking();
        var total = await query.CountAsync(ct);
        var items = await query
            .OrderByDescending(a => a.AtUtc)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(a => new PermissionAuditItem(
                a.Id, a.AtUtc, a.ActorUserId, a.ActorName, a.TargetUserId, a.TargetName,
                a.Action, a.Permission, a.Source, a.Result, a.Detail))
            .ToListAsync(ct);
        return new PagedResponse<PermissionAuditItem>(page, pageSize, total, items);
    }

    /// <summary>"@bob", the full name, or the bare ID.</summary>
    public static string Describe(BotUser? user, long userId)
        => user?.Username is { Length: > 0 } un ? $"@{un}" : user?.FullName is { Length: > 0 } fn ? $"{fn} ({userId})" : userId.ToString();

    public async Task<string> DescribeAsync(long userId, CancellationToken ct = default)
        => Describe(await db.BotUsers.AsNoTracking().FirstOrDefaultAsync(u => u.UserId == userId, ct), userId);

    private BotUser AddUserRow(long userId, long addedBy)
    {
        var user = new BotUser { UserId = userId, CreatedAtUtc = DateTime.UtcNow, AddedByUserId = addedBy };
        db.BotUsers.Add(user);
        return user;
    }

    private void AddAudit(Actor actor, long? targetId, string? targetName, string action, string? permission, string source, AccessResult result)
    {
        db.PermissionAudit.Add(new PermissionAudit
        {
            Id = Guid.CreateVersion7(),
            AtUtc = DateTime.UtcNow,
            ActorUserId = actor.UserId,
            ActorName = actor.Name,
            TargetUserId = targetId,
            TargetName = targetName,
            Action = action,
            Permission = permission,
            Source = source,
            Result = result.Status.ToString().ToLowerInvariant(),
            Detail = result.Message,
        });
    }
}

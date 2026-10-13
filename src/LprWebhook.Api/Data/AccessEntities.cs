namespace LprWebhook.Api.Data;

/// <summary>A Telegram user the bot knows about: someone who messaged it, or was added by ID by an admin.</summary>
public class BotUser
{
    /// <summary>Telegram user ID.</summary>
    public long UserId { get; set; }

    public string? Username { get; set; }
    public string? FullName { get; set; }

    /// <summary>"user" or "admin". The owner comes from config and is never stored as a role.</summary>
    public string Role { get; set; } = "user";

    public DateTime CreatedAtUtc { get; set; }
    public DateTime? LastSeenAtUtc { get; set; }
    public long? AddedByUserId { get; set; }
}

/// <summary>One command a (non-admin) user is allowed to run, e.g. "open_gate".</summary>
public class UserPermission
{
    public long UserId { get; set; }
    public string Permission { get; set; } = "";
    public long? GrantedByUserId { get; set; }
    public DateTime GrantedAtUtc { get; set; }
}

/// <summary>Every attempt to change access, including refused ones.</summary>
public class PermissionAudit
{
    public Guid Id { get; set; }
    public DateTime AtUtc { get; set; }

    public long ActorUserId { get; set; }
    public string? ActorName { get; set; }
    public long? TargetUserId { get; set; }
    public string? TargetName { get; set; }

    /// <summary>"grant", "revoke", "promote", "demote" or "add_user".</summary>
    public string Action { get; set; } = "";
    public string? Permission { get; set; }

    /// <summary>"telegram" or "web".</summary>
    public string Source { get; set; } = "";

    /// <summary>"ok", "denied" (actor not allowed) or "invalid" (bad input).</summary>
    public string Result { get; set; } = "";
    public string? Detail { get; set; }
}

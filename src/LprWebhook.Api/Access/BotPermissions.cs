namespace LprWebhook.Api.Access;

/// <summary>A bot command that can be granted to users.</summary>
public sealed record BotPermission(string Key, string Command, string Description);

/// <summary>
/// Every grantable command. To add one: add an entry here and a handler in BotCommandHandler.
/// Admins and the owner can always use all of them.
/// </summary>
public static class BotPermissions
{
    public const string OpenGate = "open_gate";

    public static readonly IReadOnlyList<BotPermission> All =
    [
        new(OpenGate, "/open_gate", "Open the gate"),
    ];

    /// <summary>Accepts "open_gate", "/open_gate" or "Open_Gate".</summary>
    public static BotPermission? Find(string? keyOrCommand)
    {
        var key = keyOrCommand?.Trim().TrimStart('/').ToLowerInvariant();
        return All.FirstOrDefault(p => p.Key == key);
    }
}

public static class Roles
{
    public const string Owner = "owner";
    public const string Admin = "admin";
    public const string User = "user";

    public static bool IsAdmin(string role) => role is Owner or Admin;
}

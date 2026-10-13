namespace LprWebhook.Api.Contracts;

public sealed record UserAccessItem(
    long UserId,
    string? Username,
    string? FullName,
    string Role,
    IReadOnlyList<string> Permissions,
    DateTime? CreatedAtUtc,
    DateTime? LastSeenAtUtc);

public sealed record PermissionAuditItem(
    Guid Id,
    DateTime AtUtc,
    long ActorUserId,
    string? ActorName,
    long? TargetUserId,
    string? TargetName,
    string Action,
    string? Permission,
    string Source,
    string Result,
    string? Detail);

public sealed record MeResponse(long UserId, string Name, string Role);

public sealed record AccessActionResponse(string Status, string Message);

public sealed record LoginRequest(string? Code);

public sealed record AddUserRequest(long UserId);

public sealed record SetRoleRequest(string? Role);

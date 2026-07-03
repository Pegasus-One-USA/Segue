namespace FHIRBridge.Application.Abstractions.Security;

public interface ICurrentUserService
{
    CurrentUserInfo CurrentUser { get; }
}

public sealed record CurrentUserInfo(
    string? ExternalUserId,
    string? Email,
    string? DisplayName,
    string[] Roles,
    bool IsAuthenticated,
    string[] Permissions = null!)
{
    public string[] Permissions { get; init; } = Permissions ?? [];

    public string AuditName =>
        string.IsNullOrWhiteSpace(Email)
            ? ExternalUserId ?? "anonymous"
            : Email;
}

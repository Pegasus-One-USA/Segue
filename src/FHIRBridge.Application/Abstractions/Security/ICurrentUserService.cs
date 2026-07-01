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
    bool IsAuthenticated)
{
    public string AuditName =>
        string.IsNullOrWhiteSpace(Email)
            ? ExternalUserId ?? "anonymous"
            : Email;
}

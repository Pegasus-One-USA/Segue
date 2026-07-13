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
    string[] Permissions = null!,
    // Ambient request context for audit trail entries. Populated by the API's HTTP-aware ICurrentUserService;
    // stays null for the Worker's non-interactive SystemCurrentUserService (no HTTP context to source them from).
    string? IpAddress = null,
    string? UserAgent = null,
    string? CorrelationId = null)
{
    public string[] Permissions { get; init; } = Permissions ?? [];

    public string AuditName =>
        string.IsNullOrWhiteSpace(Email)
            ? ExternalUserId ?? "anonymous"
            : Email;
}

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
    string? CorrelationId = null,
    // The calling browser's own origin (scheme://host[:port]) for this request — read from the Origin
    // header, falling back to Referer's scheme+authority. Used to build links embedded in outbound
    // email (invite/password-reset) so they always point at whatever portal the admin is actually
    // using, instead of a hand-maintained per-environment config value that can go stale (see
    // UserManagementService.BuildInviteLink / LocalAuthService.BuildResetLink). A caller MUST validate
    // this against the same allowlist CORS itself trusts (IAllowedCorsOriginsCache) before using it for
    // anything — it's an unauthenticated, client-supplied header, not attested by anything.
    string? RequestOrigin = null,
    // The internal Users.Id GUID for this request's caller, when resolvable (Local JWT carries it as the "uid"
    // claim; Entra tokens get it looked up by ExternalUserId at token-validation time — see
    // FhirBridgeAuthenticationExtensions). Null for non-interactive/system contexts (Worker) or a caller whose
    // internal User row can't be resolved.
    Guid? UserId = null)
{
    public string[] Permissions { get; init; } = Permissions ?? [];

    // Provenance columns (CreatedBy/ModifiedBy/DeletedBy, and every other call site that stamps a "who did this"
    // string) key off this single property, so preferring UserId here — rather than at each stamping call site —
    // is what makes every one of them store the actual internal user id instead of an email/claim string.
    // IUserDisplayNameResolver is the read-side counterpart: it turns a stored GUID (or an older, pre-conversion
    // email/string row) back into a display name for list screens.
    public string AuditName =>
        UserId is { } userId
            ? userId.ToString()
            : string.IsNullOrWhiteSpace(Email)
                ? ExternalUserId ?? "anonymous"
                : Email;
}

using System.Security.Claims;

namespace FHIRBridge.Application.Security;

public static class CurrentUserClaimReader
{
    private static readonly string[] ExternalIdClaimTypes =
    [
        "oid",
        "http://schemas.microsoft.com/identity/claims/objectidentifier",
        ClaimTypes.NameIdentifier,
        "sub"
    ];

    private static readonly string[] EmailClaimTypes =
    [
        ClaimTypes.Email,
        "preferred_username",
        "upn",
        "email"
    ];

    private static readonly string[] DisplayNameClaimTypes =
    [
        ClaimTypes.Name,
        "name"
    ];

    private static readonly string[] RoleClaimTypes =
    [
        ClaimTypes.Role,
        "roles",
        "role"
    ];

    /// <summary>The internal Users.Id GUID claim ("uid") — set by JwtAccessTokenIssuer for Local tokens, and by
    /// FhirBridgeAuthenticationExtensions' Entra OnTokenValidated hook (looked up by ExternalUserId) for Entra
    /// tokens. Absent on legacy/still-outstanding tokens issued before this claim existed.</summary>
    private const string InternalUserIdClaimType = "uid";

    public static string? GetExternalUserId(ClaimsPrincipal principal)
    {
        return GetFirstClaimValue(principal, ExternalIdClaimTypes);
    }

    public static Guid? GetUserId(ClaimsPrincipal principal)
    {
        var raw = principal.Claims.FirstOrDefault(c => c.Type == InternalUserIdClaimType)?.Value;
        return Guid.TryParse(raw, out var id) ? id : null;
    }

    public static string? GetEmail(ClaimsPrincipal principal)
    {
        return GetFirstClaimValue(principal, EmailClaimTypes);
    }

    public static string? GetDisplayName(ClaimsPrincipal principal)
    {
        return GetFirstClaimValue(principal, DisplayNameClaimTypes);
    }

    public static string[] GetRoles(ClaimsPrincipal principal)
    {
        return principal.Claims
            .Where(claim => RoleClaimTypes.Contains(claim.Type, StringComparer.OrdinalIgnoreCase))
            .SelectMany(claim => claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string? GetFirstClaimValue(ClaimsPrincipal principal, IReadOnlyCollection<string> claimTypes)
    {
        return principal.Claims
            .FirstOrDefault(claim => claimTypes.Contains(claim.Type, StringComparer.OrdinalIgnoreCase))?
            .Value;
    }
}

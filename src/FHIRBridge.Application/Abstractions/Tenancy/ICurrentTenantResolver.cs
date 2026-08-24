namespace FHIRBridge.Application.Abstractions.Tenancy;

/// <summary>
/// Resolves an authenticated user's TenantId on demand — deliberately NOT a JWT claim, mirroring
/// <c>IUserPermissionsProvider</c>'s exact reasoning (see its remarks): a value embedded in a long-lived
/// access token would go stale if the user's tenant ever changed, and every login path (local, MFA,
/// refresh, magic link, SSO) already funnels through the same claim set via JwtAccessTokenIssuer — adding
/// tenant resolution here instead means zero changes to any of them. Looked up fresh (with short-lived
/// caching, not trusted for the lifetime of the access token) from the already-present "uid" claim.
/// </summary>
public interface ICurrentTenantResolver
{
    /// <summary>Null only if the user row itself can't be found (should not happen for an authenticated
    /// caller) — TenantId itself is a required column once the AddTenant migration completes.</summary>
    Task<Guid?> ResolveTenantIdAsync(Guid userId, CancellationToken cancellationToken);
}

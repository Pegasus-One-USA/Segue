namespace FHIRBridge.Application.Abstractions.Security;

/// <summary>
/// Resolves a user's current, effective permission codes on demand — the replacement for embedding them
/// as JWT claims (see <c>JwtAccessTokenIssuer</c>). A user's permission set can now number in the hundreds
/// (dynamically-discovered per-vendor/per-destination-type codes on top of the seeded set), which made the
/// old "one JWT claim per permission code" approach overflow the browser's ~4096-byte limit for the
/// access-token cookie once a role like SuperAdmin accumulated ~120-150 codes — every subsequent request
/// then had no credential to send and 401'd.
///
/// Implementations should look this up fresh (with short-lived caching, not a value trusted for the
/// lifetime of the access token) so a role or per-user permission change takes effect within a bounded,
/// short window instead of only on the user's next login.
/// </summary>
public interface IUserPermissionsProvider
{
    Task<IReadOnlyList<string>> GetEffectivePermissionCodesAsync(Guid userId, CancellationToken cancellationToken);
}

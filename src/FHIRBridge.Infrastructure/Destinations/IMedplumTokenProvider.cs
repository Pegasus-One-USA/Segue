namespace FHIRBridge.Infrastructure.Destinations;

/// <summary>
/// Mints (and caches) a Medplum OAuth2 bearer token for server-to-server access via the <c>client_credentials</c>
/// grant (1-hour tokens, no refresh). Supports both credential shapes: a symmetric <c>client_secret</c> and the SMART
/// Backend Services <c>private_key_jwt</c> assertion (RS384 / JWKS), selected by the
/// <see cref="MedplumClientCredential"/> subtype. See docs/backend/15-medplum-integration-plan.md §3.
/// </summary>
public interface IMedplumTokenProvider
{
    /// <summary>
    /// Returns a valid bearer access token for the given client, reusing a cached one until shortly before it
    /// expires. Thread-safe; concurrent callers for the same client share one in-flight token request. The
    /// <paramref name="credential"/> subtype determines whether a <c>client_secret</c> or a signed JWT assertion is sent.
    /// </summary>
    Task<string> GetAccessTokenAsync(
        string tokenUrl,
        MedplumClientCredential credential,
        CancellationToken cancellationToken);
}

using FHIRBridge.Domain.Enums;

namespace FHIRBridge.Domain.ValueObjects;

public sealed class SourceAuthenticationConfiguration
{
    private SourceAuthenticationConfiguration()
    {
    }

    public SourceAuthenticationConfiguration(
        AuthenticationType authenticationType,
        string? clientId,
        string? tokenEndpoint,
        string[] scopes,
        SecretReference? clientSecret,
        SecretReference? privateKey,
        string? keyId,
        string? jwksUrl = null,
        string[]? discoveredScopes = null,
        string? practiceId = null,
        string? authPlacement = null)
    {
        AuthenticationType = authenticationType;
        ClientId = clientId;
        TokenEndpoint = tokenEndpoint;
        Scopes = scopes;
        ClientSecret = clientSecret;
        PrivateKey = privateKey;
        KeyId = keyId;
        JwksUrl = jwksUrl;
        DiscoveredScopes = discoveredScopes;
        PracticeId = practiceId;
        AuthPlacement = authPlacement;
    }

    public AuthenticationType AuthenticationType { get; private set; }
    public string? ClientId { get; private set; }
    public string? TokenEndpoint { get; private set; }
    public string[] Scopes { get; private set; } = [];
    public SecretReference? ClientSecret { get; private set; }
    public SecretReference? PrivateKey { get; private set; }
    public string? KeyId { get; private set; }
    /// <summary>The URL Epic (or another EHR) actually fetches this connection's JWK Set from — FHIRBridge's own
    /// hosted <c>.well-known/jwks.json</c> for a Generated/Imported key, or an admin-typed external URL for a key
    /// stored/served elsewhere. Purely informational to FHIRBridge (it never fetches this URL itself); persisted
    /// so the portal can show back whatever was actually registered with the EHR instead of only ever guessing.</summary>
    public string? JwksUrl { get; private set; }
    /// <summary>The scopes Epic (or another EHR) actually granted the app, as returned by the last successful
    /// "Discover" token exchange against the backend-auth-scopes probe. Null until Discover has run once;
    /// purely informational — never used to build the actual token request (see <see cref="Scopes"/> for that).</summary>
    public string[]? DiscoveredScopes { get; private set; }
    /// <summary>Tenant-scoping identifier some vendors require on every FHIR request (e.g. athenahealth's numeric
    /// practice id, from which the connector builds the <c>ah-practice=Organization/a-1.Practice-{id}</c> reference).
    /// Null for vendors that don't need per-tenant request scoping — every existing connection is unaffected.</summary>
    public string? PracticeId { get; private set; }
    /// <summary>Where the client-credentials grant places client id/secret — <c>"post"</c> (form body, the default)
    /// or <c>"basic"</c> (Authorization header), for vendors whose token endpoint rejects one of the two. Null
    /// behaves as <c>"post"</c>, unchanged from before this field existed.</summary>
    public string? AuthPlacement { get; private set; }

    /// <summary>Returns a copy with only <see cref="Scopes"/> replaced — everything else carries over unchanged.</summary>
    public SourceAuthenticationConfiguration WithScopes(string[] scopes) => new(
        AuthenticationType, ClientId, TokenEndpoint, scopes, ClientSecret, PrivateKey, KeyId, JwksUrl, DiscoveredScopes, PracticeId, AuthPlacement);

    /// <summary>Returns a copy with only <see cref="DiscoveredScopes"/> replaced — everything else carries over unchanged.</summary>
    public SourceAuthenticationConfiguration WithDiscoveredScopes(string[]? discoveredScopes) => new(
        AuthenticationType, ClientId, TokenEndpoint, Scopes, ClientSecret, PrivateKey, KeyId, JwksUrl, discoveredScopes, PracticeId, AuthPlacement);
}

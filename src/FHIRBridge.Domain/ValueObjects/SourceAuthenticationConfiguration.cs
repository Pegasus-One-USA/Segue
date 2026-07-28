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
        string? jwksUrl = null)
    {
        AuthenticationType = authenticationType;
        ClientId = clientId;
        TokenEndpoint = tokenEndpoint;
        Scopes = scopes;
        ClientSecret = clientSecret;
        PrivateKey = privateKey;
        KeyId = keyId;
        JwksUrl = jwksUrl;
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

    /// <summary>Returns a copy with only <see cref="Scopes"/> replaced — everything else carries over unchanged.</summary>
    public SourceAuthenticationConfiguration WithScopes(string[] scopes) => new(
        AuthenticationType, ClientId, TokenEndpoint, scopes, ClientSecret, PrivateKey, KeyId, JwksUrl);
}

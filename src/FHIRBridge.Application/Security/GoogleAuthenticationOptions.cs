namespace FHIRBridge.Application.Security;

/// <summary>
/// Binds the <c>Authentication:Google</c> configuration section. Google sign-in is OFF by default. When
/// enabled, the SSO token-exchange endpoints validate Google-issued ID tokens (signature via Google's
/// JWKS, issuer <c>accounts.google.com</c>, audience = <see cref="ClientId"/>) and mint a FHIRBridge JWT.
/// </summary>
public sealed class GoogleAuthenticationOptions
{
    /// <summary>When false (default) Google token exchange is rejected.</summary>
    public bool Enabled { get; set; }

    /// <summary>OAuth 2.0 client id of the Google application; used as the required token audience.</summary>
    public string? ClientId { get; set; }
}

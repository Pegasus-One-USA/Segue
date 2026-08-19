using FHIRBridge.Domain.ValueObjects;

namespace FHIRBridge.Application.Security;

/// <summary>
/// Binds the <c>Authentication:Saml</c> configuration section. SAML 2.0 SSO is OFF by default. When
/// enabled, the API acts as a SAML Service Provider against a single configured hospital/corporate
/// identity provider (Okta, ADFS, PingFederate, etc.): it issues an AuthnRequest, receives the signed
/// assertion at the ACS endpoint, and mints a FHIRBridge JWT for the linked/known user — the same
/// token-exchange shape already used for Entra/Google via <see cref="ExternalIdentity"/>.
/// </summary>
public sealed class SamlAuthenticationOptions
{
    /// <summary>When false (default) the SAML endpoints reject every request.</summary>
    public bool Enabled { get; set; }

    /// <summary>This application's own SAML entity id (the Service Provider identifier registered with the IdP).</summary>
    public string? ServiceProviderEntityId { get; set; }

    /// <summary>The identity provider's entity id, as published in its metadata.</summary>
    public string? IdentityProviderEntityId { get; set; }

    /// <summary>The IdP's Single Sign-On (redirect-binding) endpoint the AuthnRequest is sent to.</summary>
    public string? SingleSignOnUrl { get; set; }

    /// <summary>The IdP's base64-encoded X.509 signing certificate, used to validate assertion signatures. Public — not a secret.</summary>
    public string? IdentityProviderCertificate { get; set; }

    /// <summary>Where the browser is sent after a successful ACS callback (the portal's login-complete route).</summary>
    public string? PortalRedirectUrl { get; set; }

    /// <summary>Where the browser is sent after a failed ACS callback (the portal's login page, with an error indicator).</summary>
    public string? PortalErrorRedirectUrl { get; set; }

    /// <summary>
    /// Reference to this Service Provider's own signing certificate/private key (Key Vault-backed, same
    /// pattern as <see cref="SourceAuthenticationConfiguration.PrivateKey"/>). Only needed if the IdP
    /// requires signed AuthnRequests or encrypted assertions.
    /// </summary>
    public SecretReference? ServiceProviderSigningKey { get; set; }
}

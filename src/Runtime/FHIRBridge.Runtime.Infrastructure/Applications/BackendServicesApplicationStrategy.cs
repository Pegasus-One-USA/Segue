using FHIRBridge.Runtime.Application.Abstractions.Applications;
using FHIRBridge.Runtime.Application.DTOs;
using FHIRBridge.SharedKernel.Enums;
using FHIRBridge.Runtime.Infrastructure.Auth;

namespace FHIRBridge.Runtime.Infrastructure.Applications;

/// <summary>
/// SMART Backend Services: machine-to-machine, no human, <c>client_credentials</c> grant requesting <c>system/</c>
/// scopes. No browser round-trip, no PKCE, no refresh token. Two client-authentication mechanisms exist among
/// vendors using this application type: Epic signs a <c>private_key_jwt</c> assertion (RS384/JWKS); athenahealth
/// (and other client-secret backend vendors) instead sends the plain client id/secret over the client-credentials
/// grant. Dispatched here by which credential the source actually carries — not a vendor switch, so any future
/// client-secret backend vendor works without touching this class.
/// </summary>
public sealed class BackendServicesApplicationStrategy : SourceApplicationStrategyBase
{
    private readonly SmartBackendServicesTokenProvider _jwtAssertion;
    private readonly OAuth2ClientCredentialsTokenProvider _clientSecret;

    public BackendServicesApplicationStrategy(
        SmartBackendServicesTokenProvider jwtAssertion,
        OAuth2ClientCredentialsTokenProvider clientSecret)
    {
        _jwtAssertion = jwtAssertion;
        _clientSecret = clientSecret;
    }

    public override ApplicationType Handles => ApplicationType.Backend;

    // Machine-to-machine: no per-user patient/practitioner context is ever established.
    public override FhirContextBindingKind BindingResourceType => FhirContextBindingKind.None;

    public override SourceApplicationDescriptor Describe() => new(
        ApplicationType.Backend,
        SmartOAuthFlows.ClientCredentials,
        SmartScopePrefixes.System,
        IsInteractive: false,
        RequiresPkce: false,
        RequiresRedirectUri: false,
        RequiresLaunchToken: false,
        RequiresTrustedIssuerAllowList: false,
        SupportsRefreshToken: false,
        PortalAudienceSlug: "backend-system");

    public override Task<string> GetAccessTokenAsync(FhirSourceConfiguration source, CancellationToken cancellationToken) =>
        UsesJwtAssertion(source)
            ? _jwtAssertion.GetAccessTokenAsync(source, cancellationToken)
            : _clientSecret.GetAccessTokenAsync(source, cancellationToken);

    public override Task<string?> GetGrantedScopeAsync(FhirSourceConfiguration source, CancellationToken cancellationToken) =>
        UsesJwtAssertion(source)
            ? _jwtAssertion.GetGrantedScopeAsync(source, cancellationToken)
            : _clientSecret.GetGrantedScopeAsync(source, cancellationToken);

    private static bool UsesJwtAssertion(FhirSourceConfiguration source) =>
        !string.IsNullOrWhiteSpace(source.PrivateKeyPem);

    protected override void ValidateCore(FhirSourceConfiguration source, List<string> errors)
    {
        RequireClientId(source, errors);
        RequireTokenEndpoint(source, errors);
        RequirePracticeIdForAthenahealth(source, errors);
        if (string.IsNullOrWhiteSpace(source.PrivateKeyPem) && string.IsNullOrWhiteSpace(source.ClientSecret))
        {
            errors.Add("Backend Services requires either a signing private key (private_key_jwt) or a client secret (client_credentials).");
        }
    }
}

using FHIRBridge.Runtime.Application.Abstractions.Applications;
using FHIRBridge.Runtime.Application.DTOs;
using FHIRBridge.SharedKernel.Enums;
using FHIRBridge.Runtime.Infrastructure.Auth;

namespace FHIRBridge.Runtime.Infrastructure.Applications;

/// <summary>
/// SMART Backend Services: machine-to-machine, no human. The client authenticates with a signed
/// <c>private_key_jwt</c> assertion over the <c>client_credentials</c> grant and requests <c>system/</c> scopes.
/// There is no browser round-trip, no PKCE, and no refresh token. Token acquisition delegates to the SMART Backend
/// Services JWT provider.
/// </summary>
public sealed class BackendServicesApplicationStrategy : SourceApplicationStrategyBase
{
    private readonly EpicAccessTokenProvider _smartBackendServices;

    public BackendServicesApplicationStrategy(EpicAccessTokenProvider smartBackendServices)
    {
        _smartBackendServices = smartBackendServices;
    }

    public override ApplicationType Handles => ApplicationType.Backend;

    public override SourceApplicationDescriptor Describe() => new(
        ApplicationType.Backend,
        SmartOAuthFlows.ClientCredentials,
        SmartScopePrefixes.System,
        IsInteractive: false,
        RequiresPkce: false,
        RequiresRedirectUri: false,
        RequiresLaunchToken: false,
        RequiresTrustedIssuerAllowList: false,
        SupportsRefreshToken: false);

    public override Task<string> GetAccessTokenAsync(FhirSourceConfiguration source, CancellationToken cancellationToken) =>
        _smartBackendServices.GetAccessTokenAsync(source, cancellationToken);

    protected override void ValidateCore(FhirSourceConfiguration source, List<string> errors)
    {
        RequireClientId(source, errors);
        RequireTokenEndpoint(source, errors);
        if (string.IsNullOrWhiteSpace(source.PrivateKeyPem))
        {
            errors.Add("Backend Services requires a signing private key (private_key_jwt).");
        }
    }
}

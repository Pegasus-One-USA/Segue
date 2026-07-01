using FHIRBridge.Runtime.Application.Abstractions.Applications;
using FHIRBridge.Runtime.Application.DTOs;
using FHIRBridge.Runtime.Domain.Enums;
using FHIRBridge.Runtime.Infrastructure.Auth;

namespace FHIRBridge.Runtime.Infrastructure.Applications;

/// <summary>
/// Provider EHR launch: the app is launched from inside the EHR with an opaque <c>launch</c> token and issuer
/// (<c>iss</c>). Uses the <c>authorization_code</c> grant with mandatory PKCE, the <c>launch</c> scope plus
/// <c>user/</c> scopes, and requires a trusted-issuer allow-list (validated before redirect to defeat token
/// phishing). Token acquisition delegates to the vendor-neutral interactive SMART provider.
/// </summary>
public sealed class EhrLaunchApplicationStrategy : SourceApplicationStrategyBase
{
    private readonly SmartAuthorizationCodeTokenProvider _interactive;

    public EhrLaunchApplicationStrategy(SmartAuthorizationCodeTokenProvider interactive)
    {
        _interactive = interactive;
    }

    public override ApplicationType Handles => ApplicationType.EhrLaunch;

    public override SourceApplicationDescriptor Describe() => new(
        ApplicationType.EhrLaunch,
        SmartOAuthFlows.AuthorizationCode,
        SmartScopePrefixes.User,
        IsInteractive: true,
        RequiresPkce: true,
        RequiresRedirectUri: true,
        RequiresLaunchToken: true,
        RequiresTrustedIssuerAllowList: true,
        SupportsRefreshToken: true);

    public override Task<string> GetAccessTokenAsync(FhirSourceConfiguration source, CancellationToken cancellationToken) =>
        _interactive.GetAccessTokenAsync(source, cancellationToken);

    protected override void ValidateCore(FhirSourceConfiguration source, List<string> errors)
    {
        RequireClientId(source, errors);
        RequireAuthorizationEndpoint(source, errors);
        RequireTokenEndpoint(source, errors);

        // The trusted-iss allow-list is mandatory for EHR launch (incoming iss must be validated before redirect).
        // It is validated here once the launch-context configuration is added to the source model (build item 4).
    }
}

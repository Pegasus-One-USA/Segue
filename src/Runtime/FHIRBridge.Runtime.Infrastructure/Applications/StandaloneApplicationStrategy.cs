using FHIRBridge.Runtime.Application.Abstractions.Applications;
using FHIRBridge.Runtime.Application.DTOs;
using FHIRBridge.SharedKernel.Enums;
using FHIRBridge.Runtime.Infrastructure.Auth;

namespace FHIRBridge.Runtime.Infrastructure.Applications;

/// <summary>
/// Provider standalone: a clinician launches the app directly (no EHR launch context). Uses the
/// <c>authorization_code</c> grant with mandatory PKCE and requests <c>user/</c> scopes; the patient context is
/// established after login (Epic <c>launch/patient</c> picker or app-driven search). A refresh token keeps the
/// session alive. Token acquisition delegates to the vendor-neutral interactive SMART provider.
/// </summary>
public sealed class StandaloneApplicationStrategy : SourceApplicationStrategyBase
{
    private readonly SmartAuthorizationCodeTokenProvider _interactive;

    public StandaloneApplicationStrategy(SmartAuthorizationCodeTokenProvider interactive)
    {
        _interactive = interactive;
    }

    public override ApplicationType Handles => ApplicationType.Standalone;

    public override SourceApplicationDescriptor Describe() => new(
        ApplicationType.Standalone,
        SmartOAuthFlows.AuthorizationCode,
        SmartScopePrefixes.User,
        IsInteractive: true,
        RequiresPkce: true,
        RequiresRedirectUri: true,
        RequiresLaunchToken: false,
        RequiresTrustedIssuerAllowList: false,
        SupportsRefreshToken: true);

    public override Task<string> GetAccessTokenAsync(FhirSourceConfiguration source, CancellationToken cancellationToken) =>
        _interactive.GetAccessTokenAsync(source, cancellationToken);

    public override Task<string?> GetPatientContextAsync(FhirSourceConfiguration source, CancellationToken cancellationToken) =>
        _interactive.GetPatientContextAsync(source, cancellationToken);

    public override Task<string?> GetResolvedBaseUrlAsync(FhirSourceConfiguration source, CancellationToken cancellationToken) =>
        _interactive.GetResolvedBaseUrlAsync(source, cancellationToken);

    protected override void ValidateCore(FhirSourceConfiguration source, List<string> errors)
    {
        RequireClientId(source, errors);
        RequireAuthorizationEndpoint(source, errors);
        RequireTokenEndpoint(source, errors);
    }
}

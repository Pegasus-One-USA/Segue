using FHIRBridge.Runtime.Application.Abstractions.Applications;
using FHIRBridge.Runtime.Application.DTOs;
using FHIRBridge.SharedKernel.Enums;
using FHIRBridge.Runtime.Infrastructure.Auth;

namespace FHIRBridge.Runtime.Infrastructure.Applications;

/// <summary>
/// Patient / member standalone: the patient signs in with their own portal (e.g. MyChart) credentials, so the
/// patient context is implicit from the login rather than picked. Uses the <c>authorization_code</c> grant as a
/// public client with mandatory PKCE and requests <c>patient/</c> (self-only) scopes. A refresh token keeps the
/// session alive; revocation happens in the portal's connected-apps view. Token acquisition delegates to the
/// vendor-neutral interactive SMART provider.
/// </summary>
public sealed class PatientApplicationStrategy : SourceApplicationStrategyBase
{
    private readonly SmartAuthorizationCodeTokenProvider _interactive;

    public PatientApplicationStrategy(SmartAuthorizationCodeTokenProvider interactive)
    {
        _interactive = interactive;
    }

    public override ApplicationType Handles => ApplicationType.Patient;

    public override SourceApplicationDescriptor Describe() => new(
        ApplicationType.Patient,
        SmartOAuthFlows.AuthorizationCode,
        SmartScopePrefixes.Patient,
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

using FHIRBridge.Runtime.Application.Abstractions.Applications;
using FHIRBridge.Runtime.Application.DTOs;
using FHIRBridge.SharedKernel.Enums;
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

    // Patient-kind binding — but see EnforceEhrLaunchBindingAsync (InteractiveSourceAuthorizationService): for
    // THIS application type, the binding is inverted from every other application type. Identity is
    // "{iss}:{patient}" rather than a caller-supplied identity, precisely because a provider is expected to
    // launch into MANY different patients' charts over time — that's the normal, intended use of Provider EHR
    // Launch, not a security violation to guard against. Instead, this pins a given (issuer, patient) pair to
    // whichever caller identity first established it (tracked in its own CallerIdentity column, not ResourceId) —
    // a DIFFERENT caller identity later authorizing against that SAME already-bound patient is rejected.
    public override FhirContextBindingKind BindingResourceType => FhirContextBindingKind.Patient;

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

    public override Task<string?> GetPatientContextAsync(FhirSourceConfiguration source, CancellationToken cancellationToken) =>
        _interactive.GetPatientContextAsync(source, cancellationToken);

    public override Task<string?> GetResolvedBaseUrlAsync(FhirSourceConfiguration source, CancellationToken cancellationToken) =>
        _interactive.GetResolvedBaseUrlAsync(source, cancellationToken);

    public override Task DiscardTokenAsync(FhirSourceConfiguration source, CancellationToken cancellationToken) =>
        _interactive.DiscardTokenAsync(source, cancellationToken);

    protected override void ValidateCore(FhirSourceConfiguration source, List<string> errors)
    {
        RequireClientId(source, errors);
        RequireAuthorizationEndpoint(source, errors);
        RequireTokenEndpoint(source, errors);

        // The trusted-iss allow-list is mandatory for EHR launch (incoming iss must be validated before redirect).
        // It is validated here once the launch-context configuration is added to the source model (build item 4).
    }
}

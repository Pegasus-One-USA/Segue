using FHIRBridge.SharedKernel.Enums;

namespace FHIRBridge.Runtime.Application.Abstractions.Applications;

/// <summary>
/// The invariant shape of an <see cref="ApplicationType"/>: the OAuth grant it uses, the scope prefix it requests
/// under, which configuration surfaces apply (PKCE, redirect URI, launch token, trusted-issuer allow-list,
/// refresh tokens), and the portal-facing audience slug it serializes as. This drives both the wizard (which fields
/// to show) and the runtime flow, so a caller learns everything it needs from the strategy's descriptor without
/// switching on the enum.
/// </summary>
/// <param name="PortalAudienceSlug">The machine slug the portal's EHR-vendor source form uses for this type — the
/// backend half of the portal's own APPLICATION_TYPE_TO_AUDIENCE map (wizard.service.ts). Lives here, owned by each
/// strategy, rather than in a switch at the call site: a new application type must not require editing an unrelated
/// endpoint to be serializable (enforced by ApplicationTypeDispatchTests).</param>
public sealed record SourceApplicationDescriptor(
    ApplicationType ApplicationType,
    string OAuthFlow,
    string ScopePrefix,
    bool IsInteractive,
    bool RequiresPkce,
    bool RequiresRedirectUri,
    bool RequiresLaunchToken,
    bool RequiresTrustedIssuerAllowList,
    bool SupportsRefreshToken,
    string PortalAudienceSlug);

/// <summary>
/// Which FHIR resource type (if any) a user-to-FHIR-context binding enforces for this application type. <c>None</c>
/// for a type that establishes no durable per-user context (Backend Services). Resolved per <see cref="ISourceApplicationStrategy"/>
/// implementation — never switched on <see cref="ApplicationType"/> directly, see <c>ApplicationTypeDispatchTests</c>.
/// </summary>
public enum FhirContextBindingKind
{
    None,
    Patient,
    Practitioner
}

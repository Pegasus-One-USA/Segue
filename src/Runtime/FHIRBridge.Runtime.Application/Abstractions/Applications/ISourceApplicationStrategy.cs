using FHIRBridge.Runtime.Application.DTOs;
using FHIRBridge.SharedKernel.Enums;

namespace FHIRBridge.Runtime.Application.Abstractions.Applications;

/// <summary>
/// Encapsulates everything that varies by <see cref="ApplicationType"/> for a source connection: the OAuth flow it
/// describes, its configuration validation, and how it acquires an access token. There is one strategy per type,
/// resolved from <see cref="ISourceApplicationStrategyRegistry"/>. Adding a new application type is a new
/// implementation plus a single registration — the composite token provider, pipeline, and callback handling stay
/// closed for modification (enforced by the no-switch-on-ApplicationType architecture test).
/// </summary>
/// <remarks>
/// The interactive-flow members — authorize-request construction, callback completion and context resolution — are
/// added onto this same interface as those flows are built out, so dispatch stays registry-based.
/// </remarks>
public interface ISourceApplicationStrategy
{
    /// <summary>The single application type this strategy is responsible for.</summary>
    ApplicationType Handles { get; }

    /// <summary>Which FHIR resource type a user-to-FHIR-context binding enforces for this application type — see
    /// <see cref="FhirContextBindingKind"/>.</summary>
    FhirContextBindingKind BindingResourceType { get; }

    /// <summary>The invariant description of this application type's OAuth flow and configuration surfaces.</summary>
    SourceApplicationDescriptor Describe();

    /// <summary>Validates a source configuration against this application type's rules (the wizard "Validate" step).</summary>
    SourceApplicationValidationResult Validate(FhirSourceConfiguration source);

    /// <summary>Acquires an access token for a source configured as this application type.</summary>
    Task<string> GetAccessTokenAsync(FhirSourceConfiguration source, CancellationToken cancellationToken);

    /// <summary>
    /// Returns the launched patient id for interactive types that establish a patient context (EHR launch,
    /// standalone, patient), or null for types that do not (Backend Services). Source connectors use it to scope a
    /// fetch to the launched patient.
    /// </summary>
    Task<string?> GetPatientContextAsync(FhirSourceConfiguration source, CancellationToken cancellationToken);

    /// <summary>
    /// Returns the FHIR base URL the launch establishing this session actually resolved to (the source connection's
    /// own configured base URL, or a hospital/organization EhrEndpoint override), for interactive types — or null for
    /// types that never override it (Backend Services).
    /// </summary>
    Task<string?> GetResolvedBaseUrlAsync(FhirSourceConfiguration source, CancellationToken cancellationToken);

    /// <summary>Discards any cached token for this source (no-op for types that acquire tokens on demand rather than
    /// caching an interactive session, e.g. Backend Services).</summary>
    Task DiscardTokenAsync(FhirSourceConfiguration source, CancellationToken cancellationToken);

    /// <summary>
    /// Returns the SMART scope string actually granted by the authorization server for this session, or null when
    /// it isn't known yet (no token minted/stored, or the server didn't echo a <c>scope</c> back). Distinct from
    /// <see cref="FhirSourceConfiguration.Scopes"/>, which is only what was requested/configured — the grant can be
    /// narrower. Every strategy supports this the same way it acquires a token: Backend Services mints (or reuses a
    /// cached) token on demand since there is no user to wait on; the interactive types read back whatever their
    /// stored session already carries.
    /// </summary>
    Task<string?> GetGrantedScopeAsync(FhirSourceConfiguration source, CancellationToken cancellationToken);
}

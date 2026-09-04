using FHIRBridge.Application.DTOs;
using FHIRBridge.Domain.Enums;
using FHIRBridge.SharedKernel.Enums;

namespace FHIRBridge.Application.Services;

/// <summary>
/// Generates the SMART-on-FHIR scope set for a source from its application type, selected resource types, and scope
/// version — the single server-side source of truth (so API-created sources get the same scopes the wizard shows).
/// When the source's advertised <c>scopes_supported</c> is provided, the result is validated against it.
/// </summary>
public interface IScopeGeneratorService
{
    /// <param name="vendor">
    /// The source's vendor, used only to look up a <see cref="VendorScopeProfile"/> for the vendors whose
    /// <c>system/</c> scope vocabulary isn't the uniform version-suffix shape (see
    /// <see cref="VendorScopeCatalog"/>). Optional and null-by-default: a caller that doesn't supply it, or a
    /// vendor with no registered profile, gets exactly the previous behaviour.
    /// </param>
    GeneratedScopesDto Generate(
        ApplicationType? applicationType,
        IEnumerable<string> resourceTypes,
        string scopeVersion,
        bool scopeVersionDetected,
        IReadOnlyCollection<string>? supportedScopes,
        SourceSystemType? vendor = null);
}

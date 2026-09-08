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
    /// <param name="isGroupExport">
    /// True for a Group <c>$export</c> (bulk) source. Adds the vendor's Group read scope (e.g. <c>system/Group.read</c>)
    /// on top of the per-resource scopes — required by SMART bulk (and eCW's Backend Authentication guide) for a
    /// <c>Group/{id}/$export</c>. Group is otherwise deliberately excluded from the per-resource scope set (see
    /// <see cref="VendorScopeCatalog"/>) so it can't leak into a Backend Single Patient grant, so this flag is the
    /// only path that requests it.
    /// </param>
    GeneratedScopesDto Generate(
        ApplicationType? applicationType,
        IEnumerable<string> resourceTypes,
        string scopeVersion,
        bool scopeVersionDetected,
        IReadOnlyCollection<string>? supportedScopes,
        SourceSystemType? vendor = null,
        bool isGroupExport = false);
}

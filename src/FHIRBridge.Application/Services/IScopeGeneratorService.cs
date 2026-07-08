using FHIRBridge.Application.DTOs;
using FHIRBridge.SharedKernel.Enums;

namespace FHIRBridge.Application.Services;

/// <summary>
/// Generates the SMART-on-FHIR scope set for a source from its application type, selected resource types, and scope
/// version — the single server-side source of truth (so API-created sources get the same scopes the wizard shows).
/// When the source's advertised <c>scopes_supported</c> is provided, the result is validated against it.
/// </summary>
public interface IScopeGeneratorService
{
    GeneratedScopesDto Generate(
        ApplicationType? applicationType,
        IEnumerable<string> resourceTypes,
        string scopeVersion,
        bool scopeVersionDetected,
        IReadOnlyCollection<string>? supportedScopes);
}

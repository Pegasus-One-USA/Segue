using FHIRBridge.Application.DTOs;

namespace FHIRBridge.Application.Abstractions.Terminology;

public interface ITerminologyLookupService
{
    Task<TerminologyLookupResult?> LookupAsync(
        string system,
        string code,
        CancellationToken cancellationToken);

    /// <summary>Searches every locally-synced code system (ignoring which one the caller expected) for a
    /// matching code — used by <see cref="Services.Transforms.Nodes.CodeableConceptBuilderNode"/>'s opt-in
    /// cross-system auto-detect, when a code isn't found under its configured system. Deliberately local-only:
    /// unlike <see cref="LookupAsync"/>, this never falls through to the network, since "look this code up
    /// under any system" isn't a real FHIR terminology-server operation to fall back to.</summary>
    Task<TerminologyLookupResult?> LookupAnyLocalSystemAsync(
        string code,
        CancellationToken cancellationToken);
}

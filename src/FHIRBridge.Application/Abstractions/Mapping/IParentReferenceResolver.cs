using FHIRBridge.Application.DTOs;

namespace FHIRBridge.Application.Abstractions.Mapping;

/// <summary>
/// Determines which FHIR reference field a "child" resource must map because it is configured as a
/// child of a given "parent" resource in the same <c>ResourcePipelineRoute</c> — e.g. resolves
/// <c>Observation.subject.reference</c> for a Patient parent, or <c>Observation.encounter.reference</c>
/// for an Encounter parent. Entirely metadata-driven via <see cref="IFhirElementCatalog"/>; no resource
/// type or field name is hardcoded.
/// </summary>
public interface IParentReferenceResolver
{
    /// <summary>
    /// Resolves the required reference field on <paramref name="childResourceType"/> for a parent of type
    /// <paramref name="parentResourceType"/>. Returns <c>null</c> if no reference field on the child can
    /// target that parent resource type at all (an invalid pairing).
    /// </summary>
    /// <param name="referenceFieldOverride">
    /// When set, always wins over auto-resolution — the exact FHIR path to require, for the rare case
    /// where a resource has more than one candidate reference field and the auto-picked one is wrong.
    /// </param>
    FhirElementDto? Resolve(string childResourceType, string parentResourceType, string? referenceFieldOverride = null);
}

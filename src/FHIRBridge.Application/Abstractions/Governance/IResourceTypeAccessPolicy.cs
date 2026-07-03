namespace FHIRBridge.Application.Abstractions.Governance;

/// <summary>
/// Per-resource-type RBAC: decides whether a given FHIR resource type is permitted to be accessed (e.g. allow
/// Patient but deny Observation). Consulted during governance evaluation so disallowed resource types are denied
/// before any payload is read or written.
/// </summary>
public interface IResourceTypeAccessPolicy
{
    bool IsResourceTypeAllowed(string resourceType);
}

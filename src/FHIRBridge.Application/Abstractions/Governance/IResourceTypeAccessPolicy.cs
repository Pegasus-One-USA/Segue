namespace FHIRBridge.Application.Abstractions.Governance;

/// <summary>
/// Per-resource-type RBAC: decides whether a tenant is permitted to access a given FHIR resource type (e.g. allow
/// Patient but deny Observation). Consulted during governance evaluation so disallowed resource types are denied
/// before any payload is read or written.
/// </summary>
public interface IResourceTypeAccessPolicy
{
    bool IsResourceTypeAllowed(Guid tenantId, string resourceType);
}

using FHIRBridge.Application.DTOs;

namespace FHIRBridge.Application.Services;

/// <summary>
/// Per-resource-type FHIR search criteria for a workflow's source nodes — authored by the "Criteria" button on
/// each row of the destination wizard's "Map fields" step.
/// </summary>
public interface IResourceTypeCriteriaService
{
    Task<List<ResourceTypeCriteriaDto>> ListAsync(Guid workflowId, CancellationToken cancellationToken);

    /// <summary>
    /// Appends to (or, with <see cref="SaveResourceTypeCriteriaRequest.Replace"/>, overwrites) the criteria for
    /// one resource type, creating the row if this is the first criteria for it.
    /// </summary>
    Task<ResourceTypeCriteriaDto> SaveAsync(
        Guid workflowId, SaveResourceTypeCriteriaRequest request, CancellationToken cancellationToken);

    Task DeleteAsync(
        Guid workflowId, string sourceNodeId, string resourceType, CancellationToken cancellationToken);

    /// <summary>Retires every criteria row of a workflow, for when the workflow itself is deleted.</summary>
    Task DeleteForWorkflowAsync(Guid workflowId, CancellationToken cancellationToken);
}

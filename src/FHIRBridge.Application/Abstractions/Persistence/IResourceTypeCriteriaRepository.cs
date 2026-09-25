using FHIRBridge.Domain.Entities;

namespace FHIRBridge.Application.Abstractions.Persistence;

public interface IResourceTypeCriteriaRepository
{
    /// <summary>Every criteria row for a workflow — what an extraction loads once per run.</summary>
    Task<IReadOnlyList<ResourceTypeCriteria>> ListForWorkflowAsync(Guid workflowId, CancellationToken cancellationToken);

    Task<ResourceTypeCriteria?> GetAsync(
        Guid workflowId, string sourceNodeId, string resourceType, CancellationToken cancellationToken);

    Task AddAsync(ResourceTypeCriteria criteria, CancellationToken cancellationToken);

    /// <summary>Persists changes to an already-tracked row (see <see cref="ResourceTypeCriteria.AppendCriteria"/>).</summary>
    Task SaveChangesAsync(CancellationToken cancellationToken);

    Task RemoveAsync(ResourceTypeCriteria criteria, CancellationToken cancellationToken);

    /// <summary>Retires every criteria row of a workflow — called when the workflow itself is deleted.</summary>
    Task RemoveForWorkflowAsync(Guid workflowId, CancellationToken cancellationToken);
}

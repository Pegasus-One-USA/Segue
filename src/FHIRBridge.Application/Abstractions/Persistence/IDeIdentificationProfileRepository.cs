using FHIRBridge.Domain.Entities;

namespace FHIRBridge.Application.Abstractions.Persistence;

public interface IDeIdentificationProfileRepository
{
    Task<IReadOnlyList<DeIdentificationProfile>> ListAsync(CancellationToken cancellationToken);

    Task<DeIdentificationProfile?> GetByIdAsync(Guid id, CancellationToken cancellationToken);

    Task AddAsync(DeIdentificationProfile profile, CancellationToken cancellationToken);

    /// <summary>Everything OUTSIDE <paramref name="workflowId"/> that still uses the profile — a destination
    /// connection that names it, or another workflow whose saved definition references it — as display names.
    /// Empty when only that workflow uses it, which is the one case its rules may be deleted on its behalf.</summary>
    Task<IReadOnlyList<string>> FindUsesOutsideWorkflowAsync(
        Guid profileId, Guid workflowId, CancellationToken cancellationToken);
}

using FHIRBridge.Domain.Entities;

namespace FHIRBridge.Application.Abstractions.Persistence;

public interface IDeIdentificationProfileRepository
{
    Task<IReadOnlyList<DeIdentificationProfile>> ListAsync(CancellationToken cancellationToken);

    Task<DeIdentificationProfile?> GetByIdAsync(Guid id, CancellationToken cancellationToken);

    Task AddAsync(DeIdentificationProfile profile, CancellationToken cancellationToken);

    /// <summary>
    /// Who uses the profile, split into what lies OUTSIDE <paramref name="workflowId"/> (display names) and the
    /// destination connections that are this workflow's own.
    ///
    /// A destination connection naming the profile is the workflow's own only when this workflow's saved definition
    /// references it and nothing else does — no other workflow, and no V1 route (which reads the profile from the
    /// connection). Anything else, including a connection no workflow references, counts as outside: when
    /// ownership cannot be established, the profile is treated as shared.
    /// </summary>
    Task<DeIdentificationProfileUsage> FindUsageAsync(Guid profileId, Guid workflowId, CancellationToken cancellationToken);

    /// <summary>Stops the given destination connections naming the profile (sets their profile to none).</summary>
    Task ClearFromDestinationsAsync(
        Guid profileId, IReadOnlyCollection<Guid> destinationIds, CancellationToken cancellationToken);
}

/// <summary>See <see cref="IDeIdentificationProfileRepository.FindUsageAsync"/>.</summary>
public sealed record DeIdentificationProfileUsage(
    IReadOnlyList<string> UsedElsewhere,
    IReadOnlyList<Guid> OwnDestinationIds);

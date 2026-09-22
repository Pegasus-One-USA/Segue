using FHIRBridge.Application.DTOs;
using FHIRBridge.Domain.Entities;

namespace FHIRBridge.Infrastructure.Destinations.ApiEndpoint;

/// <summary>
/// Holds each participating resource type's mapped records for one in-flight multi-resource ApiEndpoint write
/// until every EXPECTED resource type (<see cref="ApiEndpointSettings.ResourceRelations"/>) has contributed —
/// see MappedApiEndpointDestinationWriter's own remarks for why this lives entirely inside the writer (an
/// in-memory accumulator, keyed by destination + pipeline run) rather than requiring the pipeline orchestrator
/// to change how or how often it calls WriteAsync. Registered as a singleton — state is deliberately per-process,
/// not persisted; a run that's interrupted mid-way (process restart between resource types landing) simply never
/// completes and its accumulated records are dropped, exactly as any other in-flight, not-yet-durable write
/// would be if the process died before it happened.
///
/// The same "never completes" outcome also happens WITHOUT a restart, whenever a resource type this run
/// genuinely expected contributes zero records this cycle (see MappedApiEndpointDestinationWriter.WriteAsync)
/// — an ordinary, non-exotic run shape, not a failure. Left unbounded, every such run would hold its mapped
/// PHI in process memory for the rest of the process's life. The concrete implementation therefore sweeps
/// entries older than a plausible run lifetime and caps the number of distinct pending entries, logging when
/// either discards one, so an abandoned entry is bounded and observable rather than a silent, permanent leak.
/// </summary>
public interface IApiEndpointMultiResourceAccumulator
{
    /// <summary>Registers this resource type's mapped records for the given destination + pipeline run. Safe to
    /// call once per (destinationId, pipelineRunId, resourceType) — a duplicate call for the same triple
    /// (a retried route) replaces the previous entry rather than double-adding it.</summary>
    void Add(Guid destinationId, Guid pipelineRunId, MappingProfile mappingProfile, IReadOnlyCollection<MappedDestinationRecord> records);

    /// <summary>True once every resource type in <paramref name="expectedResourceTypes"/> has been added for this
    /// destination + pipeline run — <paramref name="batches"/> then holds all of them, in no particular order,
    /// and the accumulator's own copy for this (destinationId, pipelineRunId) is cleared (so a second call after
    /// completion returns false with nothing accumulated, rather than re-delivering a stale combined write).</summary>
    bool TryTakeComplete(
        Guid destinationId,
        Guid pipelineRunId,
        IReadOnlyCollection<string> expectedResourceTypes,
        out IReadOnlyList<(MappingProfile MappingProfile, IReadOnlyCollection<MappedDestinationRecord> Records)> batches);
}

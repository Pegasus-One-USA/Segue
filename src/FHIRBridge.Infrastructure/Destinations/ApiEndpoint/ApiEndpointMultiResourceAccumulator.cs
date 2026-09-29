using System.Collections.Concurrent;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Domain.Entities;
using Microsoft.Extensions.Logging;

namespace FHIRBridge.Infrastructure.Destinations.ApiEndpoint;

/// <inheritdoc cref="IApiEndpointMultiResourceAccumulator"/>
public sealed class ApiEndpointMultiResourceAccumulator : IApiEndpointMultiResourceAccumulator
{
    // Generous upper bound on how long a real, healthy run's own resource-type routes could take to all land
    // (scheduling skew, retries, a slow upstream source) — comfortably beyond that, an entry that hasn't
    // completed is abandoned (a resource type that will never arrive this run — see
    // MappedApiEndpointDestinationWriter's own remarks on WHY that happens), not merely running long. Bounds
    // how long that run's mapped PHI sits in process memory once nothing is ever going to complete it.
    private static readonly TimeSpan MaxEntryAge = TimeSpan.FromHours(2);

    // Backstop unrelated to MaxEntryAge's time-based sweep: caps how many DISTINCT (destination, run) entries
    // can be pending at once, in case an unusual volume of runs churns through the age window faster than
    // intended. Evicts the OLDEST entry first, same as the age sweep, so this never blocks a genuinely new run.
    private const int MaxPendingEntries = 1_000;

    private sealed record Key(Guid DestinationId, Guid PipelineRunId);

    private sealed record Entry(
        DateTimeOffset CreatedAtUtc,
        Dictionary<string, (MappingProfile MappingProfile, IReadOnlyCollection<MappedDestinationRecord> Records)> ByResourceType);

    // Outer dictionary keyed by (destination, run); inner by resource type. A plain `lock` around the whole
    // read-check-clear sequence in TryTakeComplete is simpler to reason about than a lock-free completeness
    // check here — multi-resource writes are, by definition, low-frequency (one destination write per resource
    // type per run, not a hot per-record path), so contention is a non-issue.
    private readonly ConcurrentDictionary<Key, Entry> _pending = new();
    private readonly object _sync = new();
    private readonly ILogger<ApiEndpointMultiResourceAccumulator> _logger;

    public ApiEndpointMultiResourceAccumulator(ILogger<ApiEndpointMultiResourceAccumulator> logger)
    {
        _logger = logger;
    }

    /// <summary>Test-only seam (see InternalsVisibleTo) — lets a test assert the cap/sweep actually bound the
    /// pending count instead of only inferring it indirectly from whether a later Add still succeeds.</summary>
    internal int PendingEntryCount => _pending.Count;

    public void Add(
        Guid destinationId,
        Guid pipelineRunId,
        MappingProfile mappingProfile,
        IReadOnlyCollection<MappedDestinationRecord> records)
    {
        var key = new Key(destinationId, pipelineRunId);
        lock (_sync)
        {
            SweepAbandonedEntries_NoLock();

            if (!_pending.TryGetValue(key, out var entry))
            {
                EvictOldestIfAtCapacity_NoLock();
                entry = new Entry(DateTimeOffset.UtcNow, new(StringComparer.OrdinalIgnoreCase));
                _pending[key] = entry;
            }

            entry.ByResourceType[mappingProfile.ResourceType] = (mappingProfile, records);
        }
    }

    public bool TryTakeComplete(
        Guid destinationId,
        Guid pipelineRunId,
        IReadOnlyCollection<string> expectedResourceTypes,
        out IReadOnlyList<(MappingProfile MappingProfile, IReadOnlyCollection<MappedDestinationRecord> Records)> batches)
    {
        var key = new Key(destinationId, pipelineRunId);
        lock (_sync)
        {
            SweepAbandonedEntries_NoLock();

            if (_pending.TryGetValue(key, out var entry)
                && expectedResourceTypes.All(rt => entry.ByResourceType.ContainsKey(rt)))
            {
                batches = entry.ByResourceType.Values.ToList();
                _pending.TryRemove(key, out _);
                return true;
            }
        }

        batches = [];
        return false;
    }

    /// <summary>Drops any entry older than <see cref="MaxEntryAge"/> — a resource type this run expected never
    /// arrived (see the class doc), so the mapped PHI it's holding has nothing left to complete for and would
    /// otherwise sit in process memory for the process's whole remaining lifetime. Called from inside the same
    /// lock every Add/TryTakeComplete already takes, so this never needs one of its own.</summary>
    private void SweepAbandonedEntries_NoLock()
    {
        var cutoffUtc = DateTimeOffset.UtcNow - MaxEntryAge;
        List<Key>? toRemove = null;

        foreach (var (key, entry) in _pending)
        {
            if (entry.CreatedAtUtc >= cutoffUtc) continue;
            toRemove ??= [];
            toRemove.Add(key);
        }

        if (toRemove is null) return;

        foreach (var key in toRemove)
        {
            if (!_pending.TryRemove(key, out var removed)) continue;
            _logger.LogWarning(
                "Discarding an incomplete multi-resource ApiEndpoint write for destination {DestinationId}, " +
                "pipeline run {PipelineRunId}: it never received all expected resource types within {MaxEntryAge} " +
                "and is being dropped rather than held indefinitely. Resource types received: {ReceivedResourceTypes}.",
                key.DestinationId,
                key.PipelineRunId,
                MaxEntryAge,
                string.Join(", ", removed.ByResourceType.Keys));
        }
    }

    /// <summary>Backstop for a burst of distinct runs arriving faster than <see cref="MaxEntryAge"/> would ever
    /// sweep them — evicts the single oldest pending entry so a new one always has room, rather than letting the
    /// dictionary grow past <see cref="MaxPendingEntries"/> in the (already swept-for) worst case.</summary>
    private void EvictOldestIfAtCapacity_NoLock()
    {
        if (_pending.Count < MaxPendingEntries) return;

        var oldest = _pending.OrderBy(kvp => kvp.Value.CreatedAtUtc).First();
        if (!_pending.TryRemove(oldest.Key, out var removed)) return;

        _logger.LogWarning(
            "Discarding the oldest incomplete multi-resource ApiEndpoint write for destination {DestinationId}, " +
            "pipeline run {PipelineRunId}: the pending-entry cap ({MaxPendingEntries}) was reached. Resource " +
            "types received: {ReceivedResourceTypes}.",
            oldest.Key.DestinationId,
            oldest.Key.PipelineRunId,
            MaxPendingEntries,
            string.Join(", ", removed.ByResourceType.Keys));
    }
}

using System.Text.Json;

namespace FHIRBridge.Runtime.Application.Workflows.Storage;

/// <summary>
/// Default, non-durable <see cref="IWorkflowNodeResourceHistoryRecorder"/>. The SQL-backed recorder overrides
/// this in the composing host (see <c>AddWorkflowSqlPersistence</c>).
/// </summary>
public sealed class InMemoryWorkflowNodeResourceHistoryRecorder : IWorkflowNodeResourceHistoryRecorder
{
    private readonly List<(Guid WorkflowRunId, WorkflowNodeRunPayloadDto Dto)> _payloads = [];
    private readonly object _gate = new();

    public Task RecordNodeOutputAsync(
        Guid workflowRunId,
        Guid workflowNodeRunId,
        string nodeType,
        string contract,
        object? payload,
        CancellationToken cancellationToken)
    {
        // Mirrors the SQL-backed recorder: counts only, never the payload itself (see
        // EfWorkflowNodeResourceHistoryRecorder.SummarizePayload for why the old ICollection test never matched).
        var items = (payload as System.Collections.IEnumerable)
            ?? payload?.GetType().GetProperties()
                .FirstOrDefault(property =>
                    typeof(System.Collections.IEnumerable).IsAssignableFrom(property.PropertyType)
                    && property.PropertyType != typeof(string))
                ?.GetValue(payload) as System.Collections.IEnumerable;

        int? itemCount = null;
        SortedDictionary<string, int>? byType = null;
        if (items is not null)
        {
            var count = 0;
            foreach (var item in items)
            {
                count++;
                if (item?.GetType().GetProperty("ResourceType")?.GetValue(item) is string resourceType
                    && !string.IsNullOrWhiteSpace(resourceType))
                {
                    byType ??= new SortedDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    byType[resourceType] = byType.TryGetValue(resourceType, out var current) ? current + 1 : 1;
                }
            }

            itemCount = count;
        }

        var resourceTypeCountsJson = byType is null ? null : JsonSerializer.Serialize(byType);
        var deliveryDetailJson = string.Equals(contract, "DestinationWriteResult", StringComparison.Ordinal)
            ? JsonSerializer.Serialize(payload)
            : null;

        lock (_gate)
        {
            _payloads.Add((workflowRunId, new WorkflowNodeRunPayloadDto(
                Guid.NewGuid(),
                workflowNodeRunId,
                nodeType,
                contract,
                itemCount,
                resourceTypeCountsJson,
                deliveryDetailJson,
                DateTimeOffset.UtcNow)));
        }

        return Task.CompletedTask;
    }

    public Task<WorkflowPagedResult<WorkflowNodeRunPayloadDto>> GetPagedAsync(
        Guid workflowRunId,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var matching = _payloads
                .Where(entry => entry.WorkflowRunId == workflowRunId)
                .Select(entry => entry.Dto)
                .OrderBy(dto => dto.RecordedAtUtc)
                .ToList();

            var take = Math.Clamp(pageSize, 1, 200);
            var skip = Math.Max(0, (page - 1) * take);

            return Task.FromResult(new WorkflowPagedResult<WorkflowNodeRunPayloadDto>(
                matching.Skip(skip).Take(take).ToList(),
                matching.Count,
                page,
                take));
        }
    }

    /// <summary>This non-durable recorder never sees <c>WorkflowNodeRun</c> rows (status/error live on the
    /// entity persisted by the SQL-backed store), so it can only ever report the successes it recorded —
    /// failed/cancelled nodes aren't representable here. Callers that need real per-node status should use the
    /// SQL-backed recorder (<c>AddWorkflowSqlPersistence</c>).</summary>
    public Task<WorkflowPagedResult<WorkflowNodeRunHistoryDto>> GetNodeRunHistoryPagedAsync(
        Guid workflowRunId, int page, int pageSize, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var matching = _payloads
                .Where(entry => entry.WorkflowRunId == workflowRunId)
                .Select(entry => entry.Dto)
                .OrderBy(dto => dto.RecordedAtUtc)
                .ToList();

            var take = Math.Clamp(pageSize, 1, 200);
            var skip = Math.Max(0, (page - 1) * take);

            // Named arguments: the two trailing JSON strings were previously passed positionally and in the
            // wrong order, putting delivery detail into ResourceTypeCountsJson and vice versa. Naming them
            // makes that class of mistake impossible to repeat as the record grows.
            // WorkflowNodeId has no meaning here — this recorder stores payloads keyed by node RUN and never
            // sees the definition node — so it is left empty rather than invented.
            var items = matching.Skip(skip).Take(take).Select(dto => new WorkflowNodeRunHistoryDto(
                WorkflowNodeRunId: dto.WorkflowNodeRunId,
                WorkflowNodeId: Guid.Empty,
                NodeType: dto.NodeType,
                Rank: 0,
                SubRank: 0,
                Status: "Succeeded",
                ErrorMessage: null,
                StartedAt: dto.RecordedAtUtc,
                CompletedAt: dto.RecordedAtUtc,
                Contract: dto.Contract,
                ItemCount: dto.ItemCount,
                ResourceTypeCountsJson: dto.ResourceTypeCountsJson,
                DeliveryDetailJson: dto.DeliveryDetailJson)).ToList();

            return Task.FromResult(new WorkflowPagedResult<WorkflowNodeRunHistoryDto>(items, matching.Count, page, take));
        }
    }

    /// <summary>Counts only, matching the SQL-backed store — no node output is retained by either.</summary>
    public Task<WorkflowNodeRunPayloadDetailDto?> GetNodeRunPayloadAsync(
        Guid workflowRunId, Guid workflowNodeRunId, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var match = _payloads
                .Where(entry => entry.WorkflowRunId == workflowRunId && entry.Dto.WorkflowNodeRunId == workflowNodeRunId)
                .Select(entry => entry.Dto)
                .OrderBy(dto => dto.RecordedAtUtc)
                .FirstOrDefault();

            return Task.FromResult(match is null
                ? null
                : new WorkflowNodeRunPayloadDetailDto(
                    match.WorkflowNodeRunId, match.Contract, match.ItemCount, match.ResourceTypeCountsJson, match.DeliveryDetailJson));
        }
    }

    /// <summary>This non-durable recorder never sees <c>FieldLineageEntry</c> rows either (those are written by
    /// the Worker's lineage-capture consumer straight to SQL, not through this recorder) — always empty here.
    /// Callers that need real field lineage should use the SQL-backed recorder (<c>AddWorkflowSqlPersistence</c>).</summary>
    public Task<WorkflowPagedResult<FieldLineageChainDto>> GetFieldLineagePagedAsync(
        Guid workflowRunId, int page, int pageSize, FieldLineageFilter? filter, CancellationToken cancellationToken)
    {
        return Task.FromResult(new WorkflowPagedResult<FieldLineageChainDto>([], 0, page, Math.Clamp(pageSize, 1, 200)));
    }

    public Task<LineageSummaryDto> GetLineageSummaryAsync(Guid workflowRunId, CancellationToken cancellationToken)
    {
        return Task.FromResult(new LineageSummaryDto(0, 0, 0, 0));
    }

    public Task<IReadOnlyList<ResourceTypeSummaryDto>> GetLineageResourceTreeAsync(Guid workflowRunId, CancellationToken cancellationToken)
    {
        return Task.FromResult<IReadOnlyList<ResourceTypeSummaryDto>>([]);
    }

    // Empty, like the other lineage reads above: this recorder keeps node payloads only and never held field
    // lineage, so there is nothing to break down.
    // Empty: this recorder holds node payloads only and has no access to workflow configuration.
    public Task<IReadOnlyList<ConfiguredResourceTypeRulesDto>> GetConfiguredResourceTypeRulesAsync(
        Guid workflowRunId, CancellationToken cancellationToken)
    {
        return Task.FromResult<IReadOnlyList<ConfiguredResourceTypeRulesDto>>([]);
    }

    public Task<IReadOnlyList<ConfiguredResourceTypeRulesDto>> GetConfiguredDeIdentificationRulesAsync(
        Guid workflowRunId, CancellationToken cancellationToken)
    {
        return Task.FromResult<IReadOnlyList<ConfiguredResourceTypeRulesDto>>([]);
    }

    public Task<IReadOnlyDictionary<Guid, NodeLineageBreakdownDto>> GetNodeLineageBreakdownAsync(
        Guid workflowRunId, CancellationToken cancellationToken)
    {
        return Task.FromResult<IReadOnlyDictionary<Guid, NodeLineageBreakdownDto>>(
            new Dictionary<Guid, NodeLineageBreakdownDto>());
    }
}

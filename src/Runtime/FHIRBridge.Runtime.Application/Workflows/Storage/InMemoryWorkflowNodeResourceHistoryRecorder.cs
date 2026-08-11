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
        var payloadJson = JsonSerializer.Serialize(payload);
        var itemCount = payload is System.Collections.ICollection collection ? collection.Count : (int?)null;

        lock (_gate)
        {
            _payloads.Add((workflowRunId, new WorkflowNodeRunPayloadDto(
                Guid.NewGuid(),
                workflowNodeRunId,
                nodeType,
                contract,
                payloadJson,
                itemCount,
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

            var items = matching.Skip(skip).Take(take).Select(dto => new WorkflowNodeRunHistoryDto(
                dto.WorkflowNodeRunId, dto.NodeType, 0, 0, "Succeeded", null,
                dto.RecordedAtUtc, dto.RecordedAtUtc, dto.Contract, dto.PayloadJson, dto.ItemCount)).ToList();

            return Task.FromResult(new WorkflowPagedResult<WorkflowNodeRunHistoryDto>(items, matching.Count, page, take));
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
}

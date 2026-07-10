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
}

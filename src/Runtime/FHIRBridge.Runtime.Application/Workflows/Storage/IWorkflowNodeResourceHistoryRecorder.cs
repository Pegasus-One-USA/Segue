namespace FHIRBridge.Runtime.Application.Workflows.Storage;

/// <summary>
/// Records what each node in a workflow run actually produced (fetched resources, transformed/mapped records,
/// destination write results), so the Execution History screen can show real fetch/transform/store detail per
/// run, not just per-node status. Implementations are expected to encrypt <c>PayloadJson</c> at rest, since it can
/// carry PHI (raw FHIR resources, mapped field values).
/// </summary>
public interface IWorkflowNodeResourceHistoryRecorder
{
    Task RecordNodeOutputAsync(
        Guid workflowRunId,
        Guid workflowNodeRunId,
        string nodeType,
        string contract,
        object? payload,
        CancellationToken cancellationToken);

    Task<WorkflowPagedResult<WorkflowNodeRunPayloadDto>> GetPagedAsync(
        Guid workflowRunId,
        int page,
        int pageSize,
        CancellationToken cancellationToken);
}

public sealed record WorkflowPagedResult<T>(
    IReadOnlyList<T> Items,
    int TotalCount,
    int Page,
    int PageSize);

public sealed record WorkflowNodeRunPayloadDto(
    Guid Id,
    Guid WorkflowNodeRunId,
    string NodeType,
    string Contract,
    string PayloadJson,
    int? ItemCount,
    DateTimeOffset RecordedAtUtc);

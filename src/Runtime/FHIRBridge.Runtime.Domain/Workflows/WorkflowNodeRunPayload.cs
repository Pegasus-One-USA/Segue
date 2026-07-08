namespace FHIRBridge.Runtime.Domain.Workflows;

/// <summary>
/// The actual data a node produced during a workflow run — what an extraction node fetched, what a transform
/// node produced, what a destination node wrote — captured so the Execution History screen can answer "what was
/// fetched/mapped/stored" for a workflow run, not just its status. Unlike <see cref="WorkflowNodeRun.LineageJson"/>
/// (node/contract/metadata only), <see cref="PayloadJson"/> holds the real (potentially PHI-bearing) output, so
/// implementations are expected to encrypt it at rest.
/// </summary>
public sealed class WorkflowNodeRunPayload
{
    public WorkflowNodeRunPayload(
        Guid id,
        Guid workflowRunId,
        Guid workflowNodeRunId,
        string nodeType,
        string contract,
        string payloadJson,
        int? itemCount,
        DateTimeOffset recordedAtUtc)
    {
        Id = id == Guid.Empty ? Guid.NewGuid() : id;
        WorkflowRunId = workflowRunId;
        WorkflowNodeRunId = workflowNodeRunId;
        NodeType = nodeType;
        Contract = contract;
        PayloadJson = payloadJson;
        ItemCount = itemCount;
        RecordedAtUtc = recordedAtUtc;
    }

    public Guid Id { get; }
    public Guid WorkflowRunId { get; }
    public Guid WorkflowNodeRunId { get; }
    public string NodeType { get; }
    public string Contract { get; }
    public string PayloadJson { get; }
    public int? ItemCount { get; }
    public DateTimeOffset RecordedAtUtc { get; }
}

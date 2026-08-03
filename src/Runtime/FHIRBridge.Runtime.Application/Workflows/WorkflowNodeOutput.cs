namespace FHIRBridge.Runtime.Application.Workflows;

public sealed class WorkflowNodeOutput
{
    public WorkflowNodeOutput(
        Guid nodeId,
        string nodeType,
        object? payload,
        WorkflowDataContract contract = WorkflowDataContract.None,
        IReadOnlyDictionary<string, object?>? metadata = null)
    {
        NodeId = nodeId;
        NodeType = nodeType;
        Payload = payload;
        Contract = contract;
        Metadata = metadata ?? new Dictionary<string, object?>();
    }

    public Guid NodeId { get; }

    public string NodeType { get; }

    public object? Payload { get; }

    public WorkflowDataContract Contract { get; }

    public IReadOnlyDictionary<string, object?> Metadata { get; }
}

/// <summary>Well-known <see cref="WorkflowNodeOutput.Metadata"/> keys shared between a node executor and the
/// orchestrator loop that inspects its output.</summary>
public static class WorkflowNodeOutputMetadataKeys
{
    /// <summary>Set (to the deferred <c>BulkExportJob</c> id, as a string) when a source node kicked off an async
    /// FHIR Bulk Data <c>$export</c> job instead of blocking for its full duration. The orchestrator pauses the run
    /// at this node rather than treating the output as a normal completed result.</summary>
    public const string BulkExportDeferredJobId = "bulkExportDeferredJobId";
}

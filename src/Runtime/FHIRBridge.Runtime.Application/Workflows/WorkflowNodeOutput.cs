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

    /// <summary>Set alongside <see cref="BulkExportDeferredJobId"/> to the SOURCE VENDOR's own id for that export
    /// job, parsed from its status URL (see <c>BulkRequestIds.FromStatusUrl</c>). The orchestrator records it onto
    /// the paused <c>WorkflowRun</c> so Execution History can show it and offer a live status lookup. Absent when
    /// the status URL yielded nothing parseable — which pauses the run exactly as normal, just without the id.</summary>
    public const string BulkExportRequestId = "bulkExportRequestId";

    /// <summary>Set by <c>DeIdentificationNodeExecutor</c> to a
    /// <c>Dictionary&lt;string, IReadOnlyList&lt;FHIRBridge.Application.Abstractions.Governance.DeIdentificationFieldHop&gt;&gt;</c>
    /// keyed by resource id — each resource's PreMapping redactions, so the downstream Mapping node can merge
    /// them into the same Field Lineage chain as its own PostMapping hops (see
    /// <c>MappingNodeExecutor.ApplyTransformRulesAsync</c>). Absent (not just empty) when no resource in this
    /// node's batch had anything redacted.</summary>
    public const string PreMappingRedactions = "preMappingRedactions";
}

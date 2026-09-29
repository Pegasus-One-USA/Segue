namespace FHIRBridge.Runtime.Domain.Workflows;

/// <summary>
/// What a node produced during a workflow run — how much an extraction node fetched, a transform node produced,
/// or a destination node wrote — so the Execution History screen can answer "how much was fetched/mapped/stored"
/// for a run, not just its status.
///
/// DELIBERATELY METADATA ONLY: this record carries counts and resource-type names, never the resource content
/// itself. The former PayloadJson column held whole Epic FHIR resources and was removed — encrypted-at-rest PHI
/// is still PHI, and the screens that used it only ever needed the counts, which are now recorded directly at
/// write time instead of being re-derived by parsing the stored payload back out.
/// </summary>
public sealed class WorkflowNodeRunPayload
{
    public WorkflowNodeRunPayload(
        Guid id,
        Guid workflowRunId,
        Guid workflowNodeRunId,
        string nodeType,
        string contract,
        int? itemCount,
        string? resourceTypeCountsJson,
        string? deliveryDetailJson,
        DateTimeOffset recordedAtUtc)
    {
        Id = id == Guid.Empty ? Guid.NewGuid() : id;
        WorkflowRunId = workflowRunId;
        WorkflowNodeRunId = workflowNodeRunId;
        NodeType = nodeType;
        Contract = contract;
        ItemCount = itemCount;
        ResourceTypeCountsJson = resourceTypeCountsJson;
        DeliveryDetailJson = deliveryDetailJson;
        RecordedAtUtc = recordedAtUtc;
    }

    public Guid Id { get; }
    public Guid WorkflowRunId { get; }
    public Guid WorkflowNodeRunId { get; }
    public string NodeType { get; }
    public string Contract { get; }

    /// <summary>How many items this node emitted (resources fetched, records mapped, rows written).</summary>
    public int? ItemCount { get; }

    /// <summary>Per-resource-type counts as a JSON object, e.g. {"Patient":1,"Observation":42} — resource TYPE
    /// names and totals only, no identifiers or content. Null for contracts that aren't resource batches.</summary>
    public string? ResourceTypeCountsJson { get; }

    /// <summary>A destination node's delivery metadata (records written, download URL, email envelope) as JSON —
    /// how much went where, never what was in it. Null for non-destination nodes.</summary>
    public string? DeliveryDetailJson { get; }

    public DateTimeOffset RecordedAtUtc { get; }
}

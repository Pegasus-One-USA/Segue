namespace FHIRBridge.Runtime.Domain.Exceptions;

/// <summary>
/// Thrown when a workflow's parent/cohort-seeding resource type (e.g. Patient) fails with an
/// <see cref="IResourceExtractionFailure"/> (unauthorized, or not supported by the source at all) — since every
/// sibling resource type in the same source node is scoped off that parent's extracted ids, there is no
/// partial-success path: the whole run is cancelled rather than left to fail node-by-node. Carries the same
/// resource-type/reason detail so the orchestrator can surface a specific, correlation-searchable cancellation
/// message instead of a generic failure.
/// </summary>
public sealed class WorkflowRunCancelledException : InvalidOperationException
{
    public WorkflowRunCancelledException(string resourceType, string reason, Exception innerException)
        : base(
            $"Workflow cancelled: this app cannot fetch '{resourceType}', which is required as the " +
            $"parent/cohort scope for this workflow. {reason}",
            innerException)
    {
        ResourceType = resourceType;
    }

    public string ResourceType { get; }
}

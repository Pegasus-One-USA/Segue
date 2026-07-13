namespace FHIRBridge.Runtime.Application.Workflows;

public sealed class WorkflowExecutionContext
{
    public WorkflowExecutionContext(
        Guid workflowRunId,
        string correlationId,
        IReadOnlyDictionary<string, object?>? properties = null,
        string? triggeredBy = null,
        string? triggerType = null,
        string? targetPatientId = null)
    {
        WorkflowRunId = workflowRunId == Guid.Empty ? Guid.NewGuid() : workflowRunId;
        CorrelationId = string.IsNullOrWhiteSpace(correlationId) ? WorkflowRunId.ToString("N") : correlationId;
        Properties = properties ?? new Dictionary<string, object?>();
        TriggeredBy = triggeredBy;
        TriggerType = triggerType ?? "Manual";
        TargetPatientId = targetPatientId;
    }

    public Guid WorkflowRunId { get; }

    public string CorrelationId { get; }

    public IReadOnlyDictionary<string, object?> Properties { get; }

    public string? TriggeredBy { get; }

    public string TriggerType { get; }

    /// <summary>
    /// Which patient's stored interactive OAuth session a source node should use for this run, when the workflow's
    /// source is an interactive (Standalone/EhrLaunch/Patient) connection more than one patient has ever launched
    /// against. Null (the default for every existing trigger path) preserves the pre-existing "most recently
    /// logged-in session" behavior.
    /// </summary>
    public string? TargetPatientId { get; }
}

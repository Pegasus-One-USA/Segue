namespace FHIRBridge.Runtime.Application.Workflows;

public sealed class WorkflowExecutionContext
{
    public WorkflowExecutionContext(
        Guid workflowRunId,
        string correlationId,
        IReadOnlyDictionary<string, object?>? properties = null,
        string? triggeredBy = null,
        string? triggerType = null)
    {
        WorkflowRunId = workflowRunId == Guid.Empty ? Guid.NewGuid() : workflowRunId;
        CorrelationId = string.IsNullOrWhiteSpace(correlationId) ? WorkflowRunId.ToString("N") : correlationId;
        Properties = properties ?? new Dictionary<string, object?>();
        TriggeredBy = triggeredBy;
        TriggerType = triggerType ?? "Manual";
    }

    public Guid WorkflowRunId { get; }

    public string CorrelationId { get; }

    public IReadOnlyDictionary<string, object?> Properties { get; }

    public string? TriggeredBy { get; }

    public string TriggerType { get; }
}

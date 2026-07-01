namespace FHIRBridge.Runtime.Application.Workflows;

public sealed class WorkflowExecutionContext
{
    public WorkflowExecutionContext(
        Guid tenantId,
        Guid workflowRunId,
        string correlationId,
        IReadOnlyDictionary<string, object?>? properties = null)
    {
        TenantId = tenantId;
        WorkflowRunId = workflowRunId == Guid.Empty ? Guid.NewGuid() : workflowRunId;
        CorrelationId = string.IsNullOrWhiteSpace(correlationId) ? WorkflowRunId.ToString("N") : correlationId;
        Properties = properties ?? new Dictionary<string, object?>();
    }

    public Guid TenantId { get; }

    public Guid WorkflowRunId { get; }

    public string CorrelationId { get; }

    public IReadOnlyDictionary<string, object?> Properties { get; }
}

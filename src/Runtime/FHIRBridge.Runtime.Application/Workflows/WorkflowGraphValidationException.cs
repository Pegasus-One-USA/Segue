namespace FHIRBridge.Runtime.Application.Workflows;

public sealed class WorkflowGraphValidationException : InvalidOperationException
{
    public WorkflowGraphValidationException(IReadOnlyCollection<string> errors)
        : base($"Workflow graph is invalid: {string.Join("; ", errors)}")
    {
        Errors = errors;
    }

    public IReadOnlyCollection<string> Errors { get; }
}

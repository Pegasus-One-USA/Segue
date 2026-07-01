namespace FHIRBridge.Runtime.Application.Workflows;

public sealed class WorkflowGraphValidationResult
{
    private WorkflowGraphValidationResult(IReadOnlyCollection<string> errors)
    {
        Errors = errors;
    }

    public bool IsValid => Errors.Count == 0;

    public IReadOnlyCollection<string> Errors { get; }

    public static WorkflowGraphValidationResult Success() => new([]);

    public static WorkflowGraphValidationResult Failure(IReadOnlyCollection<string> errors) => new(errors);
}

using FHIRBridge.Runtime.Domain.Workflows;

namespace FHIRBridge.Runtime.Application.Workflows.Validation;

/// <summary>Runs every registered <see cref="IWorkflowRunParameterRule"/> against one attempt's parameters.</summary>
public interface IWorkflowRunValidator
{
    Task<IReadOnlyList<WorkflowRunValidationError>> ValidateAsync(
        WorkflowDefinition workflow,
        WorkflowRunParameters parameters,
        CancellationToken cancellationToken);
}

/// <inheritdoc cref="IWorkflowRunValidator"/>
public sealed class WorkflowRunValidator : IWorkflowRunValidator
{
    private readonly IEnumerable<IWorkflowRunParameterRule> _rules;

    public WorkflowRunValidator(IEnumerable<IWorkflowRunParameterRule> rules)
    {
        _rules = rules;
    }

    public async Task<IReadOnlyList<WorkflowRunValidationError>> ValidateAsync(
        WorkflowDefinition workflow,
        WorkflowRunParameters parameters,
        CancellationToken cancellationToken)
    {
        var context = new WorkflowRunValidationContext(workflow, parameters);
        var errors = new List<WorkflowRunValidationError>();

        // Every rule runs even once one has already failed: the caller is a third-party app that has to show its
        // user what to fix, and reporting one missing parameter at a time turns one correction into several
        // round trips through an EHR sign-in.
        foreach (var rule in _rules)
        {
            errors.AddRange(await rule.ValidateAsync(context, cancellationToken));
        }

        return errors;
    }
}

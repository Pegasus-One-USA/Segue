using FHIRBridge.Runtime.Domain.Workflows;

namespace FHIRBridge.Runtime.Application.Workflows.Validation;

/// <summary>
/// The caller-supplied parameters a run will execute with, already trimmed — see
/// <see cref="WorkflowRunParameters.Normalize"/>. Validation and execution must see the SAME values, so
/// normalization happens once, up front, and the normalized instance is what both the rules below and the run
/// itself go on to use; trimming after validation would let the two disagree.
/// </summary>
public sealed record WorkflowRunParameters(
    string? PatientId,
    string? PatientSearchCriteria,
    string? CallerId,
    /// <summary>The hospital/organization the caller intends to launch against, when it is picking one from the
    /// public endpoint directory. Optional at this layer by design — a launch with no override runs against the
    /// source connection's own base URL (see OAuthController's Guid.Empty handling) — so this is validated for
    /// existence when supplied, not required.</summary>
    Guid? EhrEndpointId = null)
{
    /// <summary>Trims every supplied value and collapses whitespace-only values to null, so " " and null mean the
    /// same thing to every rule rather than one of them silently counting as "supplied". An all-zero endpoint id
    /// collapses to null for the same reason.</summary>
    public static WorkflowRunParameters Normalize(
        string? patientId, string? patientSearchCriteria, string? callerId, Guid? ehrEndpointId = null) =>
        new(Clean(patientId), Clean(patientSearchCriteria), Clean(callerId),
            ehrEndpointId == Guid.Empty ? null : ehrEndpointId);

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>One reason a run was refused, addressed to the calling app's own error UI.</summary>
/// <param name="Parameter">The parameter at fault, or the resource type a required parameter is missing for.</param>
/// <param name="Message">Plain text safe to show an end user — never carries PHI or vendor error detail.</param>
public sealed record WorkflowRunValidationError(string Parameter, string Message);

/// <summary>
/// The outcome of validating a run's parameters. <see cref="CorrelationId"/> is returned even when
/// <see cref="IsValid"/> is false: a refused attempt still produces an Execution History row, and the caller
/// needs the id to find it.
/// </summary>
public sealed record WorkflowRunValidationResult(
    bool IsValid,
    string CorrelationId,
    Guid WorkflowRunId,
    IReadOnlyList<WorkflowRunValidationError> Errors,
    WorkflowRunParameters Parameters);

/// <summary>Everything a rule may inspect. Passed as one object so adding a new input later does not change
/// every rule's signature.</summary>
/// <param name="Workflow">The definition about to run, with its nodes.</param>
/// <param name="Parameters">Already-normalized caller parameters.</param>
public sealed record WorkflowRunValidationContext(
    WorkflowDefinition Workflow,
    WorkflowRunParameters Parameters);

/// <summary>
/// One independently-registered validation rule. Rules are composed by <see cref="IWorkflowRunValidator"/> in DI
/// registration order and every rule always runs, so a caller fixing one problem is not sent round the loop to
/// discover the next.
/// <para>Deliberately a registry rather than a switch: the vendor- and resource-type-specific rules this is built
/// for ("Epic requires <c>category</c> on Observation", and its eCW/athenahealth counterparts) must be addable as
/// new classes, matching how source vendors and application types are already extended in this codebase.</para>
/// </summary>
public interface IWorkflowRunParameterRule
{
    Task<IReadOnlyList<WorkflowRunValidationError>> ValidateAsync(
        WorkflowRunValidationContext context,
        CancellationToken cancellationToken);
}

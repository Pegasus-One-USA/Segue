using FHIRBridge.Application.Abstractions.Sources;
using FHIRBridge.Runtime.Application.Abstractions.Applications;
using FHIRBridge.Runtime.Application.Workflows.Validation;

namespace FHIRBridge.Infrastructure.Workflows.Validation;

/// <summary>
/// A run whose source establishes no patient context of its own must be told which patient(s) to fetch — either a
/// <c>patientId</c> or a <c>patientSearchCriteria</c> string.
/// <para>Whether the source establishes one is read from the application strategy's
/// <see cref="ISourceApplicationStrategy.BindingResourceType"/>, never switched on the enum: <c>Patient</c>
/// (Patient Standalone, EHR Launch) means the token itself carries the launched patient, and <c>None</c> (Backend
/// Services) means the run is cohort/Group-scoped — neither needs anything from the caller. Only
/// <c>Practitioner</c> (Provider Standalone) leaves the fetch genuinely unscoped.</para>
/// <para>What this prevents: <c>ApplyPatientScopeAsync</c> falls back to an unscoped search when there is no
/// patient context and no criteria, and Epic then rejects it with business rule 59108 ("A patient is required")
/// or simply returns an empty bundle. Previously the caller learned that only after being sent through a full
/// interactive sign-in — the run had to reach Epic to discover a problem visible before it started.</para>
/// <para>Lives in Infrastructure rather than alongside the other rules in Runtime.Application because resolving a
/// workflow's application type crosses into the configuration side; the rule registry is host-composed, so where
/// a rule lives is a reference-direction detail, not part of its contract.</para>
/// </summary>
public sealed class PatientScopeRequiredRule : IWorkflowRunParameterRule
{
    private readonly IInteractiveSourceAuthorizationService _authorizationService;
    private readonly ISourceApplicationStrategyRegistry _strategyRegistry;

    public PatientScopeRequiredRule(
        IInteractiveSourceAuthorizationService authorizationService,
        ISourceApplicationStrategyRegistry strategyRegistry)
    {
        _authorizationService = authorizationService;
        _strategyRegistry = strategyRegistry;
    }

    public async Task<IReadOnlyList<WorkflowRunValidationError>> ValidateAsync(
        WorkflowRunValidationContext context,
        CancellationToken cancellationToken)
    {
        if (context.Parameters.PatientId is not null || context.Parameters.PatientSearchCriteria is not null)
        {
            return [];
        }

        var applicationType = await _authorizationService.GetWorkflowApplicationTypeAsync(
            context.Workflow.Id, cancellationToken);

        // Unresolvable application type (no source node, or a source this doesn't apply to): say nothing rather
        // than guess. WorkflowIsRunnableRule already reports a workflow with no source node, and inventing a
        // parameter requirement for a source we could not identify would block runs that are actually fine.
        if (applicationType is not { } resolved
            || !_strategyRegistry.TryResolve(resolved, out var strategy)
            || strategy.BindingResourceType != FhirContextBindingKind.Practitioner)
        {
            return [];
        }

        return
        [
            new WorkflowRunValidationError(
                "patientSearchCriteria",
                "This workflow signs in as a practitioner, so it has no patient of its own to fetch. "
                + "Enter search criteria (for example family=Smith or identifier=MRN12345), or supply a patient id.")
        ];
    }
}

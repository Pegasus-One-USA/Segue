using FHIRBridge.Domain.Entities;
using FHIRBridge.Domain.Enums;

namespace FHIRBridge.Application.Abstractions.Persistence;

/// <summary>Persistence for <see cref="TransformationRule"/>. One method per scope tier so
/// <c>EffectiveRuleResolver</c> can query exactly the tier it's currently checking, plus a general
/// <see cref="ListAsync"/> for the Rules modal's "everything configured here" view.</summary>
public interface ITransformationRuleRepository
{
    /// <summary><paramref name="sourceSystem"/> returns rows with no source restriction OR matching this
    /// source (resolver prefers the latter — same pattern as the ResourceType/DestinationType tiers'
    /// destinationField parameter).</summary>
    Task<IReadOnlyList<TransformationRule>> GetWorkflowScopedAsync(
        Guid resourcePipelineRouteId, string resourceType, string destinationField, string? sourceSystem,
        string? sourceField, CancellationToken cancellationToken);

    Task<IReadOnlyList<TransformationRule>> GetFieldScopedAsync(
        string resourceType, string destinationField, string? sourceSystem, string? sourceField,
        CancellationToken cancellationToken);

    /// <summary><paramref name="destinationField"/> null returns every ResourceType-scoped rule regardless of
    /// field; non-null returns rules with no field restriction OR matching this field (resolver prefers the latter).</summary>
    Task<IReadOnlyList<TransformationRule>> GetResourceTypeScopedAsync(
        string resourceType, string? destinationField, CancellationToken cancellationToken);

    Task<IReadOnlyList<TransformationRule>> GetDestinationTypeScopedAsync(
        DestinationType destinationType, string? destinationField, CancellationToken cancellationToken);

    Task<IReadOnlyList<TransformationRule>> GetGlobalScopedAsync(
        string? destinationField, CancellationToken cancellationToken);

    /// <summary>Pre-mapping (raw-JSON, by <c>SourceField</c> path) rules belonging to one de-identification
    /// profile — Global-scoped rows apply regardless of resource type, ResourceType-scoped rows apply only to
    /// the given type. Used by <c>SafeHarborDeIdentificationService</c>, not the post-mapping resolver.</summary>
    Task<IReadOnlyList<TransformationRule>> GetPreMappingRulesAsync(
        Guid deIdentificationProfileId, string resourceType, CancellationToken cancellationToken);

    /// <summary>FHIR-resource-phase rules for one resource type, resolved a tier at a time so the caller can
    /// walk Workflow → Field → ResourceType → DestinationType → Global and stop at the first tier that matches
    /// — the same "most specific tier wins outright, tiers never merge" contract as
    /// <c>IEffectiveRuleResolver</c>, but keyed on <c>SourceField</c> (the FHIR read path) instead of
    /// <c>DestinationField</c>, which a FHIR-native destination doesn't have.
    ///
    /// <paramref name="sourceField"/> null returns every rule at that tier regardless of path; non-null returns
    /// rules with no path restriction OR matching this path, and the resolver prefers the latter.</summary>
    Task<IReadOnlyList<TransformationRule>> GetFhirResourceRulesAsync(
        TransformScope scope, string resourceType, string? sourceField, DestinationType? destinationType,
        Guid? resourcePipelineRouteId, string? sourceSystem, CancellationToken cancellationToken);

    /// <summary><paramref name="executionPhase"/> null excludes only
    /// <see cref="TransformExecutionPhase.FhirResource"/> rules — NOT a filter to PostMapping. The Rules modal
    /// has always listed PostMapping and PreMapping (de-identification) rows together, so narrowing the default
    /// to one phase would silently drop de-identification rules from a screen that shows them today.</summary>
    Task<IReadOnlyList<TransformationRule>> ListAsync(
        TransformScope? scope, DestinationType? destinationType, string? resourceType, string? destinationField,
        Guid? resourcePipelineRouteId, string? sourceSystem, string? sourceField,
        TransformExecutionPhase? executionPhase, CancellationToken cancellationToken);

    /// <summary>Workflow-scoped rules authored before their workflow existed — Workflow scope with a null
    /// <c>ResourcePipelineRouteId</c>, which makes them inert until attached. See
    /// <c>ITransformationRuleService.AttachPendingRulesToWorkflowAsync</c>.</summary>
    Task<IReadOnlyList<TransformationRule>> GetPendingWorkflowRulesAsync(
        IReadOnlyCollection<DestinationType> destinationTypes, CancellationToken cancellationToken);

    /// <summary>The same pending (unattached) Workflow-scope rows as <see cref="GetPendingWorkflowRulesAsync"/>,
    /// but narrowed to ONE destination field the way the live tiers are — so a save-time check can answer "will
    /// a rule be transforming this column?" for a workflow that has no id yet.
    ///
    /// Save-time only. These rows are deliberately inert at run time (an unattached rule belongs to no pipeline,
    /// so treating a null route as "applies to anything" would fire one builder session's draft rules inside
    /// every other workflow) — <c>EffectiveRuleResolver</c> reaches this tier only when a caller explicitly
    /// opts in via its <c>includePendingWorkflowRules</c> flag, which the executors never do.</summary>
    Task<IReadOnlyList<TransformationRule>> GetPendingWorkflowScopedAsync(
        DestinationType destinationType, string resourceType, string destinationField, string? sourceSystem,
        string? sourceField, CancellationToken cancellationToken);

    Task<TransformationRule?> GetByIdAsync(Guid id, CancellationToken cancellationToken);

    Task AddAsync(TransformationRule rule, CancellationToken cancellationToken);

    Task UpdateAsync(TransformationRule rule, CancellationToken cancellationToken);

    Task DeleteAsync(TransformationRule rule, CancellationToken cancellationToken);
}

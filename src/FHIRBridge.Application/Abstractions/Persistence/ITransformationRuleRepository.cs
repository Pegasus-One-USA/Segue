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
    /// <param name="destinationType">The destination the values are being written to. Required because a
    /// workflow does NOT imply one destination: replace a destination, or add a second of another type, and
    /// a rule authored for the first would otherwise still be returned for the second's column of the same
    /// name — which is how a Concatenation rule written against SQL Server reappeared, already applied, on a
    /// freshly added PostgreSQL destination. A row with no DestinationType recorded still matches anything,
    /// so rules written before this column was populated keep working rather than silently stopping.
    /// Null means "any destination", for a caller genuinely asking across all of them — see
    /// <c>ITransformationRuleService.GetRuleImpactSummaryAsync</c>, which counts how many routes already
    /// override a field and has no one destination in view.</param>
    Task<IReadOnlyList<TransformationRule>> GetWorkflowScopedAsync(
    /// <param name="destinationConfigurationId">The destination being written to, when the caller knows it.
    /// This is what separates two destinations of the SAME type: replacing a PostgreSQL destination with
    /// another one leaves every type-based check matching, so the new destination inherited the old one's
    /// rules. A rule that recorded no configuration id still matches anything, so rules written before the
    /// column existed keep applying.</param>
        Guid resourcePipelineRouteId, DestinationType? destinationType, Guid? destinationConfigurationId,
        string resourceType, string destinationField, string? sourceSystem, string? sourceField,
        CancellationToken cancellationToken);

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
    /// <param name="owner">
    /// The provenance value (<c>CreatedBy</c>, i.e. <c>CurrentUserInfo.AuditName</c>) whose pending rules may
    /// be returned. Pending rows carry no workflow and no node, so before this existed the ONLY filter was the
    /// destination type — meaning a builder session abandoned months ago handed its rules to whoever next saved
    /// a workflow writing to the same destination type, in a different workflow and under a different user.
    /// CreatedBy is a REQUIRED column stamped by AuditingSaveChangesInterceptor from CurrentUserInfo.AuditName,
    /// so no persisted rule is unowned and the narrowing leaves no gap. Note the grain: AuditName identifies a
    /// USER, not a browser session, so one person with two builder tabs open on the same destination type still
    /// shares pending rules between them — narrower than before, not zero.
    /// Null <paramref name="owner"/> disables the check entirely, for callers with no user context.
    /// </param>
    Task<IReadOnlyList<TransformationRule>> GetPendingWorkflowRulesAsync(
        IReadOnlyCollection<DestinationType> destinationTypes, string? owner, CancellationToken cancellationToken);

    /// <summary>Deletes the pending (unattached) Workflow-scope rules <paramref name="owner"/> authored against
    /// the given destination types — what the builder calls when the destination node those rules were written
    /// for is removed from the canvas and no other node of that type remains to own them.</summary>
    /// <remarks>
    /// Scoped by owner for the same reason the read is (see <see cref="GetPendingWorkflowRulesAsync"/>): an
    /// unscoped delete would destroy another session's drafts rather than merely borrowing them, which is the
    /// same defect with a worse ending. Attached rules are never touched — removing a node from an unsaved
    /// canvas must not reach into a workflow that already persisted these rules.
    /// </remarks>
    Task<int> DeletePendingWorkflowRulesAsync(
        IReadOnlyCollection<DestinationType> destinationTypes, string? owner, CancellationToken cancellationToken);

    /// <summary>The same pending (unattached) Workflow-scope rows as <see cref="GetPendingWorkflowRulesAsync"/>,
    /// but narrowed to ONE destination field the way the live tiers are — so a save-time check can answer "will
    /// a rule be transforming this column?" for a workflow that has no id yet.
    ///
    /// Save-time only. These rows are deliberately inert at run time (an unattached rule belongs to no pipeline,
    /// so treating a null route as "applies to anything" would fire one builder session's draft rules inside
    /// every other workflow) — <c>EffectiveRuleResolver</c> reaches this tier only when a caller explicitly
    /// opts in via its <c>includePendingWorkflowRules</c> flag, which the executors never do.</summary>
    /// <param name="owner">Same ownership narrowing as <see cref="GetPendingWorkflowRulesAsync"/>, and needed
    /// here for a second reason: this tier matches on destination type + resource type + destination field
    /// alone, so a rule authored against a node that has since been DELETED is indistinguishable from one
    /// authored against the node now occupying that column — which is how a freshly created mapping shows a
    /// transformation nobody selected.</param>
    Task<IReadOnlyList<TransformationRule>> GetPendingWorkflowScopedAsync(
        DestinationType destinationType, string resourceType, string destinationField, string? sourceSystem,
        string? sourceField, string? owner, CancellationToken cancellationToken);

    /// <summary>Deletes the workflow-scoped rules belonging to one destination that is being removed from a
    /// workflow — what the builder calls when a destination node is deleted from the canvas.</summary>
    /// <param name="includeUnattributed">
    /// Whether to also take rules that record NO DestinationConfigurationId but match this destination's TYPE.
    /// Every rule authored before that column existed is in that state, so without this the cleanup would
    /// miss exactly the rules that are already stuck — but a null-id rule could equally belong to another
    /// destination of the same type, so the caller passes true only when no such destination survives.
    /// </param>
    Task<int> DeleteWorkflowRulesForDestinationAsync(
        Guid resourcePipelineRouteId, Guid destinationConfigurationId, DestinationType destinationType,
        bool includeUnattributed, CancellationToken cancellationToken);

    Task<TransformationRule?> GetByIdAsync(Guid id, CancellationToken cancellationToken);

    Task AddAsync(TransformationRule rule, CancellationToken cancellationToken);

    Task UpdateAsync(TransformationRule rule, CancellationToken cancellationToken);

    Task DeleteAsync(TransformationRule rule, CancellationToken cancellationToken);
}

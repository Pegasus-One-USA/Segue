using FHIRBridge.Application.DTOs.Transforms;
using FHIRBridge.Domain.Enums;

namespace FHIRBridge.Application.Services.Transforms;

/// <summary>
/// Application-facing surface over <see cref="Abstractions.Persistence.ITransformationRuleRepository"/> +
/// <see cref="IEffectiveRuleResolver"/> + <see cref="ITransformNodeRegistry"/>: CRUD for rule rows, and
/// resolve-then-apply previewing so the wizard/Rules modal can show exactly what will happen to a real value.
/// </summary>
public interface ITransformationRuleService
{
    Task<List<TransformationRuleDto>> ListRulesAsync(
        TransformScope? scope,
        DestinationType? destinationType,
        string? resourceType,
        string? destinationField,
        Guid? resourcePipelineRouteId,
        string? sourceSystem = null,
        string? sourceField = null,
        /// <summary>Null keeps the historical listing — PostMapping plus PreMapping de-identification rows —
        /// and excludes only FhirResource rules, which are authored against a FHIR path rather than a
        /// destination column and belong to V2's Transformation node. Pass it explicitly to list those.</summary>
        TransformExecutionPhase? executionPhase = null,
        CancellationToken cancellationToken = default);

    Task<TransformationRuleDto> SaveRuleAsync(SaveTransformationRuleRequest request, CancellationToken cancellationToken = default);

    /// <summary>Binds rules authored before their workflow existed to that workflow, once it has been saved.
    /// A builder session with no workflow id yet stores its rules at Workflow scope with a null route, which is
    /// inert; this is what makes them live. Returns how many were attached.
    ///
    /// Idempotent and safe to call on every save, which is what the builder now does: a rule left unattached
    /// (its attach call failed, or it was written before the id had propagated) is invisible to the executor's
    /// workflow-scoped resolution and so silently never transforms anything, with the wizard still showing it
    /// as configured. Rules already belonging to a workflow are skipped, never re-pointed.</summary>
    /// <summary>Deletes the caller's own pending (unattached) Workflow-scope rules for the given
    /// destination types — called when the destination node they were authored against is removed from an
    /// unsaved canvas and no other node of that type remains to own them. Returns how many were removed.</summary>
    /// <remarks>Deliberately narrow: it never touches an ATTACHED rule (those belong to a saved workflow,
    /// not to the canvas) and never touches another author's drafts.</remarks>
    /// <summary>Removes the transformation rules a destination owned, when that destination is deleted from
    /// a workflow's canvas. Returns how many were removed.</summary>
    /// <remarks>
    /// Deleting a destination is expected to take its mappings and transformations with it. It did not:
    /// nothing removed attached rules at delete time, and the save-time retirement pass keys on destination
    /// TYPE, so replacing a PostgreSQL destination with another PostgreSQL one cancelled the retirement
    /// entirely and the new destination arrived with the old one's transformations already applied.
    /// </remarks>
    Task<int> DeleteRulesForDestinationAsync(
        Guid resourcePipelineRouteId,
        Guid destinationConfigurationId,
        DestinationType destinationType,
        bool otherDestinationsOfThisTypeRemain,
        CancellationToken cancellationToken = default);

    Task<int> DeletePendingRulesAsync(
        IReadOnlyCollection<DestinationType> destinationTypes, CancellationToken cancellationToken = default);

    Task<int> AttachPendingRulesToWorkflowAsync(
        Guid workflowId,
        IReadOnlyCollection<DestinationType> destinationTypes,
        CancellationToken cancellationToken = default);

    Task DeleteRuleAsync(Guid ruleId, CancellationToken cancellationToken = default);

    Task<TransformPreviewResult> PreviewAsync(TransformPreviewRequest request, CancellationToken cancellationToken = default);

    /// <summary>The actual rule row(s) currently in effect for one field — the same Workflow → Field →
    /// ResourceType → DestinationType → Global resolution <see cref="PreviewAsync"/> uses, but returning the
    /// real, editable <see cref="TransformationRuleDto"/> (id, full config) rather than just a value trace.
    /// Lets the wizard's Rules dialog show/clone what's really running for a field with no Field-level rule of
    /// its own, instead of starting an override from blank schema defaults.</summary>
    /// <param name="workflowScopedOnly">See <see cref="IEffectiveRuleResolver.ResolveAsync"/>'s own parameter —
    /// true resolves at <see cref="TransformScope.Workflow"/> scope only, for a caller whose rules are authored
    /// per pipeline and must not inherit another workflow's.</param>
    /// <param name="includePendingWorkflowRules">See <see cref="IEffectiveRuleResolver.ResolveAsync"/>'s own
    /// parameter — true also surfaces rules authored before the workflow was first saved, so the builder can
    /// see a rule it has just attached to a field on a pipeline that has no id yet.</param>
    Task<List<TransformationRuleDto>> GetEffectiveRulesAsync(
        DestinationType destinationType,
        string resourceType,
        string destinationField,
        Guid? resourcePipelineRouteId,
        string? sourceSystem,
        string? sourceField,
        CancellationToken cancellationToken = default,
        bool workflowScopedOnly = false,
        bool includePendingWorkflowRules = false,
        // The destination configuration the column belongs to. Without it two destinations of the same type
        // are indistinguishable, so a replaced destination inherits the old one's rules.
        Guid? destinationConfigurationId = null);

    /// <summary>The config schema for every node type — what keys it reads, what control to render, and its
    /// default — so the UI never has to hand-maintain a duplicate copy of this metadata.</summary>
    IReadOnlyList<TransformNodeSchemaDto> GetNodeSchemas();

    /// <summary>Informational only (see <see cref="RuleImpactSummaryDto"/>) — how many workflows a
    /// Global/ResourceType-scoped rule at this field would affect, and how many of those already have a more
    /// specific Workflow/Field override and are therefore unaffected. Meaningless for other scopes, which
    /// already name one specific field/workflow.</summary>
    Task<RuleImpactSummaryDto> GetRuleImpactSummaryAsync(
        TransformScope scope,
        string? resourceType,
        string? destinationField,
        CancellationToken cancellationToken = default);
}

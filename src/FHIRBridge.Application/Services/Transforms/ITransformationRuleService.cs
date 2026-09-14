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

    Task DeleteRuleAsync(Guid ruleId, CancellationToken cancellationToken = default);

    Task<TransformPreviewResult> PreviewAsync(TransformPreviewRequest request, CancellationToken cancellationToken = default);

    /// <summary>The actual rule row(s) currently in effect for one field, scoped to this workflow only — see
    /// <see cref="IEffectiveRuleResolver"/>'s own doc comment: a PostMapping rule belongs to exactly one
    /// workflow, so this always resolves against that one workflow's own rules, never a tenant-wide default.
    /// Returns the real, editable <see cref="TransformationRuleDto"/> (id, full config) rather than just a value
    /// trace, so the wizard's Rules dialog can show/clone what's really running for a field.</summary>
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
        bool includePendingWorkflowRules = false);

    /// <summary>The config schema for every node type — what keys it reads, what control to render, and its
    /// default — so the UI never has to hand-maintain a duplicate copy of this metadata.</summary>
    IReadOnlyList<TransformNodeSchemaDto> GetNodeSchemas();

    /// <summary>Informational only (see <see cref="RuleImpactSummaryDto"/>) — how many workflows a
    /// Global/ResourceType-scoped rule at this field would affect, and how many of those already have a
    /// Workflow-scoped override and are therefore unaffected. Meaningless for other scopes, which already name
    /// one specific field/workflow.</summary>
    Task<RuleImpactSummaryDto> GetRuleImpactSummaryAsync(
        TransformScope scope,
        string? resourceType,
        string? destinationField,
        CancellationToken cancellationToken = default);
}

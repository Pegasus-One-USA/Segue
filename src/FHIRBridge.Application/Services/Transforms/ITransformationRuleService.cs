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
        CancellationToken cancellationToken = default);

    Task<TransformationRuleDto> SaveRuleAsync(SaveTransformationRuleRequest request, CancellationToken cancellationToken = default);

    Task DeleteRuleAsync(Guid ruleId, CancellationToken cancellationToken = default);

    Task<TransformPreviewResult> PreviewAsync(TransformPreviewRequest request, CancellationToken cancellationToken = default);

    /// <summary>The actual rule row(s) currently in effect for one field — the same Workflow → Field →
    /// ResourceType → DestinationType → Global resolution <see cref="PreviewAsync"/> uses, but returning the
    /// real, editable <see cref="TransformationRuleDto"/> (id, full config) rather than just a value trace.
    /// Lets the wizard's Rules dialog show/clone what's really running for a field with no Field-level rule of
    /// its own, instead of starting an override from blank schema defaults.</summary>
    Task<List<TransformationRuleDto>> GetEffectiveRulesAsync(
        DestinationType destinationType,
        string resourceType,
        string destinationField,
        Guid? resourcePipelineRouteId,
        string? sourceSystem,
        string? sourceField,
        CancellationToken cancellationToken = default);

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

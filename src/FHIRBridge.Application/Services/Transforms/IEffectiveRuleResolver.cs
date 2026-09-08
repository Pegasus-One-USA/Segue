using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Domain.Enums;

namespace FHIRBridge.Application.Services.Transforms;

/// <summary>
/// Answers "for this destination field, which transform rule(s) actually apply right now?" by walking the
/// 5-level scope chain — Workflow → Field → ResourceType → DestinationType → Global — most specific wins,
/// falling back up the chain when a tier has nothing configured. Whichever tier has at least one matching row
/// wins outright: tiers are never merged, so a Field-scoped rule fully replaces a broader ResourceType default
/// rather than combining with it.
/// </summary>
public interface IEffectiveRuleResolver
{
    /// <param name="workflowScopedOnly">
    /// True to resolve at <see cref="TransformScope.Workflow"/> scope and stop there — no fall-through to the
    /// tenant-wide tiers. Set by a caller whose rules are authored per pipeline, so a rule belonging to a
    /// different workflow can never apply here: those broader tiers key on (resource type, destination field),
    /// not on a workflow, so one rule authored anywhere applies everywhere that field is mapped.
    ///
    /// Defaults to false, which is the original five-tier walk. V1 pipelines (and any graph saved before the
    /// workflow id was stamped onto its nodes) keep resolving exactly as they always have — their rules live in
    /// those broader tiers and would otherwise stop firing.
    /// </param>
    Task<IReadOnlyList<TransformationRule>> ResolveAsync(
        DestinationType destinationType,
        string resourceType,
        string destinationField,
        Guid? resourcePipelineRouteId,
        string? sourceSystem,
        string? sourceField,
        CancellationToken cancellationToken,
        bool workflowScopedOnly = false);
}

public sealed class EffectiveRuleResolver : IEffectiveRuleResolver
{
    private readonly ITransformationRuleRepository _repository;

    public EffectiveRuleResolver(ITransformationRuleRepository repository)
    {
        _repository = repository;
    }

    public async Task<IReadOnlyList<TransformationRule>> ResolveAsync(
        DestinationType destinationType,
        string resourceType,
        string destinationField,
        Guid? resourcePipelineRouteId,
        string? sourceSystem,
        string? sourceField,
        CancellationToken cancellationToken,
        bool workflowScopedOnly = false)
    {
        if (resourcePipelineRouteId is not null)
        {
            var workflowRules = PreferSourceFieldSpecific(
                PreferSourceSpecific(
                    Enabled(await _repository.GetWorkflowScopedAsync(
                        resourcePipelineRouteId.Value, resourceType, destinationField, sourceSystem, sourceField, cancellationToken)),
                    sourceSystem),
                sourceField);
            if (workflowRules.Count > 0)
            {
                return OrderOnly(workflowRules);
            }

            // This pipeline's rules are the only ones that may apply to it, so "no rule here" means no rule —
            // not "look for someone else's".
            if (workflowScopedOnly)
            {
                return [];
            }
        }

        // A workflow-scoped caller with no workflow id yet (an unsaved graph) has nothing that could have been
        // authored against it, and must not inherit the tenant-wide tiers either.
        if (workflowScopedOnly)
        {
            return [];
        }

        var fieldRules = PreferSourceFieldSpecific(
            PreferSourceSpecific(
                PreferResourceTypeSpecific(
                    PreferFieldSpecific(
                        Enabled(await _repository.GetFieldScopedAsync(resourceType, destinationField, sourceSystem, sourceField, cancellationToken)),
                        destinationField),
                    resourceType),
                sourceSystem),
            sourceField);
        if (fieldRules.Count > 0)
        {
            return OrderOnly(fieldRules);
        }

        var resourceTypeRules = PreferFieldSpecific(
            Enabled(await _repository.GetResourceTypeScopedAsync(resourceType, destinationField, cancellationToken)), destinationField);
        if (resourceTypeRules.Count > 0)
        {
            return OrderOnly(resourceTypeRules);
        }

        var destinationTypeRules = PreferFieldSpecific(
            Enabled(await _repository.GetDestinationTypeScopedAsync(destinationType, destinationField, cancellationToken)), destinationField);
        if (destinationTypeRules.Count > 0)
        {
            return OrderOnly(destinationTypeRules);
        }

        var globalRules = PreferFieldSpecific(
            Enabled(await _repository.GetGlobalScopedAsync(destinationField, cancellationToken)), destinationField);
        return OrderOnly(globalRules);
    }

    /// <summary>Filters out disabled rows BEFORE a tier's "did anything match" count check — applying this only
    /// inside Order() (as the old code did) let a tier whose only match was disabled still win the tier
    /// (Count &gt; 0 on the unfiltered list), return empty after the disabled row was dropped, and never fall
    /// through to a broader tier — silently treating "disabled" as "no rule anywhere" instead of "check the
    /// next tier".</summary>
    private static IReadOnlyList<TransformationRule> Enabled(IReadOnlyList<TransformationRule> rules) =>
        rules.Where(r => r.IsEnabled).ToList();

    /// <summary>Within one tier, a row that names this exact field takes priority over a blanket
    /// (DestinationField == null) row for the same tier.</summary>
    private static IReadOnlyList<TransformationRule> PreferFieldSpecific(
        IReadOnlyList<TransformationRule> rules, string destinationField)
    {
        var fieldSpecific = rules.Where(r => r.DestinationField == destinationField).ToList();
        return fieldSpecific.Count > 0 ? fieldSpecific : rules;
    }

    /// <summary>Field-tier-only counterpart to <see cref="PreferFieldSpecific"/>'s field-name preference — a
    /// row naming this exact resource type takes priority over a blanket (ResourceType == null, "any
    /// resource") row within the Field tier, same "specific beats blanket" pattern.</summary>
    private static IReadOnlyList<TransformationRule> PreferResourceTypeSpecific(
        IReadOnlyList<TransformationRule> rules, string resourceType)
    {
        var resourceTypeSpecific = rules.Where(r => r.ResourceType == resourceType).ToList();
        return resourceTypeSpecific.Count > 0 ? resourceTypeSpecific : rules;
    }

    /// <summary>Within Field/Workflow tier rows, a row naming this exact source system takes priority over a
    /// blanket (SourceSystem == null, "any source") row — same "specific beats blanket" pattern as
    /// <see cref="PreferFieldSpecific"/>, just on the source-system dimension instead of the field dimension.</summary>
    private static IReadOnlyList<TransformationRule> PreferSourceSpecific(
        IReadOnlyList<TransformationRule> rules, string? sourceSystem)
    {
        if (sourceSystem is null)
        {
            return rules;
        }

        var sourceSpecific = rules.Where(r => r.SourceSystem == sourceSystem).ToList();
        return sourceSpecific.Count > 0 ? sourceSpecific : rules;
    }

    /// <summary>Same "specific beats blanket" preference as <see cref="PreferSourceSpecific"/>, on the source
    /// *field* dimension (e.g. "identifier.value") instead of the source *system* dimension (e.g. "Epic") —
    /// the two are independent and both narrow the same Field/Workflow tier row set.</summary>
    private static IReadOnlyList<TransformationRule> PreferSourceFieldSpecific(
        IReadOnlyList<TransformationRule> rules, string? sourceField)
    {
        if (sourceField is null)
        {
            return rules;
        }

        var sourceFieldSpecific = rules.Where(r => r.SourceField == sourceField).ToList();
        return sourceFieldSpecific.Count > 0 ? sourceFieldSpecific : rules;
    }

    private static IReadOnlyList<TransformationRule> OrderOnly(IReadOnlyList<TransformationRule> rules) =>
        rules.OrderBy(r => r.Order).ToList();
}

using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Domain.Enums;

namespace FHIRBridge.Application.Services.Transforms;

/// <summary>
/// Answers "for this destination field, which transform rule(s) actually apply right now?" A PostMapping rule
/// only ever belongs to the one workflow it was authored against (see WORKFLOW_V3_PLAN.md's requirement #4 —
/// there is no such thing as a Field/ResourceType/DestinationType/Global PostMapping rule any more): this walks
/// that workflow's own attached rules, then its still-unattached (pending) ones, and nothing else. A rule
/// belonging to a different workflow can never apply here, and an empty result means "no rule for this
/// workflow," never "fall back to some tenant-wide default" — that concept no longer exists for PostMapping.
///
/// PreMapping (Safe Harbor de-identification) is a completely separate path — <c>ITransformationRuleRepository
/// .GetPreMappingRulesAsync</c>, resolved by profile, called directly by <c>SafeHarborDeIdentificationService</c>
/// — and is untouched by this type.
/// </summary>
public interface IEffectiveRuleResolver
{
    /// <param name="includePendingWorkflowRules">
    /// True to also consider PENDING rules for this workflow — rows authored before the workflow existed, which
    /// carry no <c>ResourcePipelineRouteId</c> yet and are therefore inert at run time. Consulted only after
    /// this workflow's own already-attached rules.
    ///
    /// Save-time callers only (mapping-profile validation, and the builder UI asking "what will transform this
    /// column?"). A brand-new pipeline is drawn and its rules written before it has an id, so without this a
    /// rule the user can plainly see attached to a field is invisible to the very check that decides whether
    /// the mapping may be saved — a deadlock, since the rule cannot be attached until the workflow saves.
    ///
    /// Never set by the executors: at run time an unattached rule belongs to no pipeline, and treating a null
    /// route as "applies to anything" would fire one builder session's draft rules inside every other workflow —
    /// exactly the leak WORKFLOW_V3_PLAN.md's root cause #4 describes.
    /// </param>
    Task<IReadOnlyList<TransformationRule>> ResolveAsync(
        DestinationType destinationType,
        string resourceType,
        string destinationField,
        Guid? resourcePipelineRouteId,
        string? sourceSystem,
        string? sourceField,
        CancellationToken cancellationToken,
        bool includePendingWorkflowRules = false);
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
        bool includePendingWorkflowRules = false)
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

            // This pipeline's rules are the only ones that may apply to it, so "no rule here" means no rule.
            // Pending rows are still checked below: a workflow saved between authoring a rule and saving its
            // mapping has an id, but its draft rules are not attached yet.
            if (!includePendingWorkflowRules)
            {
                return [];
            }
        }

        if (includePendingWorkflowRules)
        {
            var pendingRules = PreferSourceFieldSpecific(
                PreferSourceSpecific(
                    Enabled(await _repository.GetPendingWorkflowScopedAsync(
                        destinationType, resourceType, destinationField, sourceSystem, sourceField, cancellationToken)),
                    sourceSystem),
                sourceField);
            if (pendingRules.Count > 0)
            {
                return OrderOnly(pendingRules);
            }
        }

        // No workflow id yet, and no pending rule matched (or wasn't asked for) — there is nothing left to
        // resolve to. There is no broader tier to fall back to any more.
        return [];
    }

    /// <summary>Filters out disabled rows before the "did anything match" check.</summary>
    private static IReadOnlyList<TransformationRule> Enabled(IReadOnlyList<TransformationRule> rules) =>
        rules.Where(r => r.IsEnabled).ToList();

    /// <summary>Within this workflow's rules, a row naming this exact source system takes priority over a
    /// blanket (SourceSystem == null, "any source") row — "specific beats blanket."</summary>
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
    /// *field* dimension (e.g. "identifier.value") instead of the source *system* dimension (e.g. "Epic").</summary>
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

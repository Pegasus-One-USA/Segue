using FHIRBridge.Domain.Entities;
using FHIRBridge.Domain.Enums;

namespace FHIRBridge.Application.Services.Transforms;

/// <summary>
/// An <see cref="IFhirResourceRuleResolver"/> backed by rules the workflow node carries itself, instead of by
/// the shared TransformationRules table (plan §3.4 / §6).
///
/// This is what removes the <c>ResourcePipelineRouteId</c> class of bug at its root. A node's rules travel with
/// the node, so there is no route id to be null, nothing to reconcile on save, no sweep that has to guess which
/// orphaned rows belong to the workflow being saved, and no way for one workflow to pick up another's rules.
/// The route id / destination type / source system arguments are accepted only to satisfy the interface and are
/// deliberately ignored: these rules already belong to exactly one node, so there is nothing left to filter by.
///
/// Resource type IS still matched, since one node's rule set can cover several resource types.
/// </summary>
public sealed class InlineFhirResourceRuleResolver : IFhirResourceRuleResolver
{
    private readonly IReadOnlyList<TransformationRule> _rules;

    public InlineFhirResourceRuleResolver(IEnumerable<TransformationRule> rules)
    {
        // Ordering is part of the contract: rules for one source field chain in Order, and the shared
        // repository resolver already returns them sorted, so anything reading either must see the same thing.
        _rules = rules
            .Where(rule => rule.IsEnabled)
            .OrderBy(rule => rule.Order)
            .ToList();
    }

    public bool IsEmpty => _rules.Count == 0;

    public Task<IReadOnlyList<string>> ResolveSourceFieldsAsync(
        string resourceType,
        DestinationType destinationType,
        Guid? resourcePipelineRouteId,
        string? sourceSystem,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<string> sourceFields = ForResourceType(resourceType)
            .Select(rule => rule.SourceField)
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .ToList();

        return Task.FromResult(sourceFields);
    }

    public Task<IReadOnlyList<TransformationRule>> ResolveAsync(
        string resourceType,
        string sourceField,
        DestinationType destinationType,
        Guid? resourcePipelineRouteId,
        string? sourceSystem,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<TransformationRule> rules = ForResourceType(resourceType)
            .Where(rule => string.Equals(rule.SourceField, sourceField, StringComparison.Ordinal))
            .ToList();

        return Task.FromResult(rules);
    }

    /// <summary>Rules that apply to this resource type. A null ResourceType means "any resource", matching the
    /// same "null is a wildcard" convention the repository-backed tiers use.</summary>
    private IEnumerable<TransformationRule> ForResourceType(string resourceType) =>
        _rules.Where(rule =>
            rule.ResourceType is null
            || string.Equals(rule.ResourceType, resourceType, StringComparison.OrdinalIgnoreCase));
}

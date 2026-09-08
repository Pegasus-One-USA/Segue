using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Domain.Enums;

namespace FHIRBridge.Application.Services.Transforms;

/// <summary>
/// The FHIR-native counterpart to <see cref="IEffectiveRuleResolver"/>: answers "for this FHIR path on this
/// resource, which rule(s) apply?" — the question that replaces "for this destination column..." once the
/// destination stores whole resources and has no columns to key on.
///
/// Keyed on <see cref="TransformationRule.SourceField"/> instead of
/// <see cref="TransformationRule.DestinationField"/>, and — unlike the PostMapping resolver — resolved at
/// <see cref="TransformScope.Workflow"/> scope ONLY, with no fall-through to the broader tiers.
///
/// That is deliberate. The tenant-wide tiers (Field/ResourceType/DestinationType/Global) make a rule apply to
/// every workflow sharing a destination type, which for transformation rules is not what anyone means: a rule
/// authored while configuring one pipeline would silently start transforming every other pipeline's resources,
/// and deleting the rule you can see does not help while another workflow's identical rule remains. A FHIR
/// transformation rule belongs to the workflow it was authored in, full stop.
///
/// Only <see cref="TransformExecutionPhase.FhirResource"/> rules are ever considered — a guarantee enforced in
/// the repository, not here, so a PostMapping rule cannot reach this path even by mistake.
/// </summary>
public interface IFhirResourceRuleResolver
{
    /// <summary>Every distinct source path that has at least one enabled rule for this resource type, so the
    /// caller knows which paths to read without walking the whole document. Returned as the paths only — the
    /// rule chain for each comes from <see cref="ResolveAsync"/>.</summary>
    Task<IReadOnlyList<string>> ResolveSourceFieldsAsync(
        string resourceType,
        DestinationType destinationType,
        Guid? resourcePipelineRouteId,
        string? sourceSystem,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<TransformationRule>> ResolveAsync(
        string resourceType,
        string sourceField,
        DestinationType destinationType,
        Guid? resourcePipelineRouteId,
        string? sourceSystem,
        CancellationToken cancellationToken);
}

public sealed class FhirResourceRuleResolver : IFhirResourceRuleResolver
{
    private readonly ITransformationRuleRepository _repository;

    public FhirResourceRuleResolver(ITransformationRuleRepository repository)
    {
        _repository = repository;
    }

    public async Task<IReadOnlyList<string>> ResolveSourceFieldsAsync(
        string resourceType,
        DestinationType destinationType,
        Guid? resourcePipelineRouteId,
        string? sourceSystem,
        CancellationToken cancellationToken)
    {
        // No workflow, no rules — a rule cannot have been authored against an id that does not exist yet.
        if (resourcePipelineRouteId is null)
        {
            return [];
        }

        var paths = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var rules = await _repository.GetFhirResourceRulesAsync(
            TransformScope.Workflow, resourceType, null, destinationType, resourcePipelineRouteId, sourceSystem,
            cancellationToken);

        foreach (var rule in rules)
        {
            if (rule.IsEnabled && rule.SourceField is { Length: > 0 } && seen.Add(rule.SourceField))
            {
                paths.Add(rule.SourceField);
            }
        }

        return paths;
    }

    public async Task<IReadOnlyList<TransformationRule>> ResolveAsync(
        string resourceType,
        string sourceField,
        DestinationType destinationType,
        Guid? resourcePipelineRouteId,
        string? sourceSystem,
        CancellationToken cancellationToken)
    {
        if (resourcePipelineRouteId is null)
        {
            return [];
        }

        // No "specific beats blanket" step on the path dimension, unlike EffectiveRuleResolver: a
        // FhirResource rule cannot HAVE a null SourceField (TransformationRule's constructor rejects one —
        // with no destination column, the read path is the rule's only key), so every row this returns
        // already names this exact path. The source-system dimension still has blanket rows and still needs
        // the preference.
        var rules = PreferSourceSpecific(
            Enabled(await _repository.GetFhirResourceRulesAsync(
                TransformScope.Workflow, resourceType, sourceField, destinationType, resourcePipelineRouteId,
                sourceSystem, cancellationToken)),
            sourceSystem);

        return rules.Count > 0 ? rules.OrderBy(r => r.Order).ToList() : [];
    }

    /// <summary>Disabled rows never reach the caller. With a single tier there is no fall-through to get wrong,
    /// but the filter still belongs here rather than at the call site so "disabled" cannot be mistaken for
    /// "configured".</summary>
    private static IReadOnlyList<TransformationRule> Enabled(IReadOnlyList<TransformationRule> rules) =>
        rules.Where(r => r.IsEnabled).ToList();

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
}

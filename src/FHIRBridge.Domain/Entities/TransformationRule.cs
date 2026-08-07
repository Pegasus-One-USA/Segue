using FHIRBridge.Domain.Enums;
using FHIRBridge.SharedKernel.Abstractions;

namespace FHIRBridge.Domain.Entities;

/// <summary>
/// One transform-node instance, scoped at one of the five <see cref="TransformScope"/> tiers. A field's
/// "effective rule set" is resolved by <c>IEffectiveRuleResolver</c> walking Workflow → Field → ResourceType →
/// DestinationType → Global and taking the most specific tier with at least one matching row — see that
/// resolver's XML doc for the exact matching rules per tier.
/// </summary>
public sealed class TransformationRule : AuditableEntity<Guid>, IHasAuditDisplayName
{
    private TransformationRule()
    {
    }

    public TransformationRule(
        TransformScope scope,
        TransformNodeType nodeType,
        string configJson,
        DestinationType? destinationType = null,
        string? resourceType = null,
        string? destinationField = null,
        Guid? resourcePipelineRouteId = null,
        string? sourceSystem = null,
        string? sourceField = null,
        int order = 0,
        NullPolicy onNull = NullPolicy.Skip,
        TransformErrorPolicy errorPolicy = TransformErrorPolicy.NullOut)
    {
        Id = Guid.NewGuid();
        Scope = scope;
        NodeType = nodeType;
        ConfigJson = configJson;
        DestinationType = destinationType;
        ResourceType = resourceType;
        DestinationField = destinationField;
        ResourcePipelineRouteId = resourcePipelineRouteId;
        SourceSystem = sourceSystem;
        SourceField = sourceField;
        Order = order;
        OnNull = onNull;
        ErrorPolicy = errorPolicy;
        IsEnabled = true;
    }

    public TransformScope Scope { get; private set; }
    public DestinationType? DestinationType { get; private set; }
    public string? ResourceType { get; private set; }

    /// <summary>What this rule writes into, once the source value has been transformed — a storage-side
    /// constraint (the destination determines the required output shape/type), independent of which source
    /// field fed it. See <see cref="SourceField"/> for the complementary source-side key.</summary>
    public string? DestinationField { get; private set; }
    public Guid? ResourcePipelineRouteId { get; private set; }

    /// <summary>Only meaningful at <see cref="TransformScope.Field"/>/<see cref="TransformScope.Workflow"/> —
    /// null means "any source"; a value (e.g. "Epic") narrows this rule to that source only, and wins over a
    /// null-SourceSystem row at the same tier when both exist (same "specific beats blanket" pattern as
    /// <see cref="DestinationField"/>). Never populated at ResourceType/DestinationType/Global — those tiers
    /// are inherently source-agnostic defaults.</summary>
    public string? SourceSystem { get; private set; }

    /// <summary>The FHIR path (e.g. "identifier.value", "name.given") this rule was authored against —
    /// complementary to <see cref="DestinationField"/>, not a replacement for it: this answers "what shape is
    /// the input in," DestinationField answers "what shape must the output become." Only meaningful at
    /// Field/Workflow scope; null means "any source field," preferring a match the same way SourceSystem does.</summary>
    public string? SourceField { get; private set; }

    public TransformNodeType NodeType { get; private set; }
    public string ConfigJson { get; private set; } = "{}";
    public int Order { get; private set; }
    public NullPolicy OnNull { get; private set; }
    public TransformErrorPolicy ErrorPolicy { get; private set; }
    public bool IsEnabled { get; private set; } = true;

    string? IHasAuditDisplayName.AuditDisplayName =>
        $"{Scope} {NodeType}" + (DestinationField is null ? string.Empty : $" → {DestinationField}");

    public void Update(string configJson, int order, NullPolicy onNull, TransformErrorPolicy errorPolicy)
    {
        ConfigJson = configJson;
        Order = order;
        OnNull = onNull;
        ErrorPolicy = errorPolicy;
    }

    public void SetEnabled(bool isEnabled) => IsEnabled = isEnabled;
}

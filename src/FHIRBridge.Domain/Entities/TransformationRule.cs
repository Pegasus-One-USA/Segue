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
        TransformErrorPolicy errorPolicy = TransformErrorPolicy.NullOut,
        string? onNullDefaultValue = null,
        TransformArrayMode arrayMode = TransformArrayMode.Whole,
        string? fhirWriteBackJsonPath = null,
        TransformExecutionPhase executionPhase = TransformExecutionPhase.PostMapping,
        Guid? deIdentificationProfileId = null,
        MappingValueType? expectedValueType = null)
    {
        if (executionPhase == TransformExecutionPhase.PreMapping)
        {
            if (scope is not (TransformScope.Global or TransformScope.ResourceType))
            {
                throw new ArgumentException(
                    "Pre-mapping rules are only valid at Global or ResourceType scope.", nameof(executionPhase));
            }

            if (deIdentificationProfileId is null)
            {
                throw new ArgumentException(
                    "Pre-mapping rules must belong to a de-identification profile.", nameof(deIdentificationProfileId));
            }
        }
        else if (deIdentificationProfileId is not null)
        {
            throw new ArgumentException(
                "A de-identification profile only applies to pre-mapping rules.", nameof(deIdentificationProfileId));
        }

        if (executionPhase == TransformExecutionPhase.FhirResource && string.IsNullOrWhiteSpace(sourceField))
        {
            // SourceField is the ONLY key a FHIR-resource rule has: with no destination column to attach to, a
            // rule that doesn't say which path to read has nothing to act on, and would silently never fire.
            throw new ArgumentException(
                "FHIR-resource rules must declare a SourceField — the FHIR path the rule reads.", nameof(sourceField));
        }

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
        OnNullDefaultValue = onNullDefaultValue;
        ArrayMode = arrayMode;
        FhirWriteBackJsonPath = fhirWriteBackJsonPath;
        ExecutionPhase = executionPhase;
        DeIdentificationProfileId = deIdentificationProfileId;
        ExpectedValueType = expectedValueType;
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

    /// <summary>Substitute value used when <see cref="OnNull"/> is <see cref="NullPolicy.Default"/> — the
    /// node is not run at all in that case; this value becomes the output directly.</summary>
    public string? OnNullDefaultValue { get; private set; }

    /// <summary>Whole-value (default, unchanged historical behavior) vs per-item application when the
    /// mapped value is a real collection — see <see cref="TransformArrayMode"/>.</summary>
    public TransformArrayMode ArrayMode { get; private set; }

    /// <summary>Where in the source resource's own JSON this rule's OUTPUT should be written back, for a
    /// FHIR-native destination (Aidbox/Medplum/any other <see cref="Enums.DestinationType.FhirRepository"/>
    /// config) to receive the transformed value instead of the untouched original. Null (the default) means
    /// "don't patch" — a scalar-in/scalar-out node (DateTimeFormat, NumberCast, ...) never needs this since its
    /// output already matches the source shape at the same path; a structure-building node (CodeableConceptBuilder,
    /// UnitConversion in FHIR-Quantity mode, ReferenceConstruction, ValueCodeMapping with emitCoding on) usually
    /// needs the PARENT of the field's own read path — e.g. a rule reading "code.coding.code" but building a
    /// whole CodeableConcept belongs at "code", not back into the bare code string leaf. Dot-separated segments,
    /// optional "[n]" array index per segment (no leading "$."), e.g. "code" or "component[0].valueQuantity".</summary>
    public string? FhirWriteBackJsonPath { get; private set; }

    /// <summary>Whether this rule runs after mapping (default, per-destination-field) or before mapping
    /// (raw source JSON, by <see cref="SourceField"/> path). See <see cref="TransformExecutionPhase"/>.</summary>
    public TransformExecutionPhase ExecutionPhase { get; private set; } = TransformExecutionPhase.PostMapping;

    /// <summary>Which <see cref="DeIdentificationProfile"/> this rule belongs to — only set (and required)
    /// when <see cref="ExecutionPhase"/> is <see cref="TransformExecutionPhase.PreMapping"/>.</summary>
    public Guid? DeIdentificationProfileId { get; private set; }

    public bool IsEnabled { get; private set; } = true;

    /// <summary>The data type this rule's output is expected to be, used to validate compatibility against
    /// the destination column it writes to (see <c>CreateMappingProfileRequestValidator</c>). Null means "not
    /// declared" — a legacy/unclassified rule that is silently excluded from that check rather than assumed
    /// to conflict. Descriptive metadata, not a structural part of the rule's identity, so unlike
    /// <see cref="NodeType"/>/<see cref="Scope"/>/<see cref="DestinationField"/> it may be corrected via
    /// <see cref="Update"/> without recreating the rule.</summary>
    public MappingValueType? ExpectedValueType { get; private set; }

    string? IHasAuditDisplayName.AuditDisplayName =>
        $"{Scope} {NodeType}" + (DestinationField is null ? string.Empty : $" → {DestinationField}");

    public void Update(
        string configJson,
        int order,
        NullPolicy onNull,
        TransformErrorPolicy errorPolicy,
        string? onNullDefaultValue = null,
        TransformArrayMode arrayMode = TransformArrayMode.Whole,
        string? fhirWriteBackJsonPath = null,
        MappingValueType? expectedValueType = null)
    {
        ConfigJson = configJson;
        Order = order;
        OnNull = onNull;
        ErrorPolicy = errorPolicy;
        OnNullDefaultValue = onNullDefaultValue;
        ArrayMode = arrayMode;
        FhirWriteBackJsonPath = fhirWriteBackJsonPath;
        ExpectedValueType = expectedValueType;
    }

    /// <summary>
    /// Re-points an existing rule at a different address — which resource type, field or source it applies to.
    ///
    /// <see cref="Update"/> deliberately rewrites only what a rule DOES (its config and failure handling), so
    /// for a long time saving an edited rule silently kept its original address: the authoring screens let you
    /// change the resource and source field, the save returned HTTP 200, and the row came back unchanged. The
    /// de-identification editor is the clearest case — resource and source field are the only things it lets
    /// you edit besides the mode, so "edit" appeared to do nothing at all.
    ///
    /// What a rule IS stays fixed: node type, execution phase and de-identification profile are not re-pointed
    /// here. Those decide which engine runs the rule and under whose policy, so changing them makes it a
    /// different rule that should be authored as one, rather than something an in-place edit mutates.
    /// </summary>
    public void Retarget(
        TransformScope scope,
        DestinationType? destinationType,
        string? resourceType,
        string? destinationField,
        string? sourceSystem,
        string? sourceField)
    {
        // The same two invariants the constructor enforces — an edit must not be a way around them.
        if (ExecutionPhase == TransformExecutionPhase.PreMapping
            && scope is not (TransformScope.Global or TransformScope.ResourceType))
        {
            throw new ArgumentException(
                "Pre-mapping rules are only valid at Global or ResourceType scope.", nameof(scope));
        }

        if (ExecutionPhase == TransformExecutionPhase.FhirResource && string.IsNullOrWhiteSpace(sourceField))
        {
            throw new ArgumentException(
                "FHIR-resource rules must declare a SourceField — the FHIR path the rule reads.", nameof(sourceField));
        }

        Scope = scope;
        DestinationType = destinationType;
        ResourceType = resourceType;
        DestinationField = destinationField;
        SourceSystem = sourceSystem;
        SourceField = sourceField;
    }

    /// <summary>Binds a rule authored before its workflow existed to that workflow, once it does.
    ///
    /// A builder session can author rules before the workflow has ever been saved and so has no id — the rule
    /// is stored at <see cref="TransformScope.Workflow"/> scope with a null
    /// <see cref="ResourcePipelineRouteId"/>, which is inert: the resolver matches on that id, so a null one
    /// can never apply to any run. This is what makes it apply, and only ever from null — a rule already bound
    /// to a workflow is never silently moved to a different one.</summary>
    public void AttachToWorkflow(Guid resourcePipelineRouteId)
    {
        if (Scope != TransformScope.Workflow)
        {
            throw new InvalidOperationException(
                "Only a workflow-scoped rule can be attached to a workflow.");
        }

        if (ResourcePipelineRouteId is not null)
        {
            throw new InvalidOperationException(
                "This rule already belongs to a workflow; re-pointing it would silently move another pipeline's rule.");
        }

        ResourcePipelineRouteId = resourcePipelineRouteId;
    }

    public void SetEnabled(bool isEnabled) => IsEnabled = isEnabled;
}

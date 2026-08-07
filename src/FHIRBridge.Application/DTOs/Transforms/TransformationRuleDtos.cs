using FHIRBridge.Domain.Enums;

namespace FHIRBridge.Application.DTOs.Transforms;

public sealed record TransformationRuleDto(
    Guid Id,
    TransformScope Scope,
    DestinationType? DestinationType,
    string? ResourceType,
    string? DestinationField,
    Guid? ResourcePipelineRouteId,
    string? SourceSystem,
    string? SourceField,
    TransformNodeType NodeType,
    Dictionary<string, string> Config,
    int Order,
    NullPolicy OnNull,
    TransformErrorPolicy ErrorPolicy,
    bool IsEnabled);

/// <summary><see cref="Id"/> null creates a new rule; supplying an existing id updates it in place.</summary>
public sealed record SaveTransformationRuleRequest(
    Guid? Id,
    TransformScope Scope,
    TransformNodeType NodeType,
    Dictionary<string, string> Config,
    DestinationType? DestinationType = null,
    string? ResourceType = null,
    string? DestinationField = null,
    Guid? ResourcePipelineRouteId = null,
    string? SourceSystem = null,
    string? SourceField = null,
    int Order = 0,
    NullPolicy OnNull = NullPolicy.Skip,
    TransformErrorPolicy ErrorPolicy = TransformErrorPolicy.NullOut,
    bool IsEnabled = true);

/// <summary>Resolve-and-apply a sample value through whatever rule chain is currently in effect for one field —
/// backs both the wizard's "auto-applied on add" behavior and the Rules modal's live preview.
/// <paramref name="SourceSystem"/>/<paramref name="SourceField"/> null means "resolve as if no source-specific
/// override could apply" — pass the current pipeline's actual source vendor (e.g. "Epic") and/or the mapped
/// FHIR path (e.g. "identifier.value") to let a source-specific Field/Workflow rule win.</summary>
public sealed record TransformPreviewRequest(
    DestinationType DestinationType,
    string ResourceType,
    string DestinationField,
    object? SampleValue,
    Guid? ResourcePipelineRouteId = null,
    string? SourceSystem = null,
    string? SourceField = null);

public sealed record TransformStepTrace(
    TransformNodeType NodeType,
    TransformScope Scope,
    object? InputValue,
    object? OutputValue,
    bool Success,
    string? Error);

public sealed record TransformPreviewResult(
    object? FinalValue,
    TransformScope? EffectiveScope,
    IReadOnlyList<TransformStepTrace> Steps);

/// <summary>One config key a node type reads, so the UI can render a proper control (dropdown/text/checkbox)
/// instead of a raw JSON textarea — see <see cref="Services.Transforms.TransformNodeConfigSchemas"/> for the
/// per-node-type field lists this is generated from.</summary>
public sealed record TransformConfigFieldSchema(
    string Key,
    string Label,
    string InputKind, // "text" | "select" | "checkbox"
    IReadOnlyList<string>? Options,
    string? DefaultValue,
    // Shown as grey example text inside an empty text box — never submitted as the actual value, unlike
    // DefaultValue. Used for fields that are correctly, intentionally blank by default (e.g. an optional
    // override) but still deserve an example so the person configuring the rule isn't guessing at the format.
    string? Placeholder = null);

public sealed record TransformNodeSchemaDto(
    TransformNodeType NodeType,
    string Label,
    IReadOnlyList<TransformConfigFieldSchema> Fields);

using FHIRBridge.Domain.Enums;

namespace FHIRBridge.Application.Validation;

/// <summary>Structured detail attached (via <c>ValidationFailure.CustomState</c>) to a mapping-profile save
/// failure caused by an applicable <c>TransformationRule</c> whose declared <c>ExpectedValueType</c> doesn't
/// match the destination column it would run against — e.g. a Global <c>NumberCast</c> rule with
/// ExpectedValueType=Integer applying to a column typed as String. Lets the portal list exactly which rule(s)
/// are the problem and offer a workflow-level override, instead of only showing the message text.</summary>
public sealed record TransformationRuleTypeConflict(
    Guid RuleId,
    TransformScope Scope,
    TransformNodeType NodeType,
    string? DestinationField,
    MappingValueType ExpectedValueType,
    string ActualColumnType);

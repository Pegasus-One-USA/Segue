namespace FHIRBridge.Domain.Enums;

/// <summary>Default <see cref="MappingValueType"/> the portal pre-fills when authoring a rule of a given
/// <see cref="TransformNodeType"/> — a starting point the author can override per rule via
/// <c>TransformationRule.ExpectedValueType</c>, not an enforced constraint. A node type maps to <c>null</c>
/// when its output shape genuinely depends on the data/config rather than the node type alone (e.g.
/// concatenation, array flattening, null-default substitution) — those require the author to pick explicitly
/// rather than risk a wrong guess.</summary>
public static class TransformNodeTypeDefaults
{
    public static readonly IReadOnlyDictionary<TransformNodeType, MappingValueType?> ExpectedValueTypeByNodeType =
        new Dictionary<TransformNodeType, MappingValueType?>
        {
            [TransformNodeType.DateTimeFormat] = MappingValueType.DateTime,
            [TransformNodeType.NumberCast] = MappingValueType.Decimal,
            [TransformNodeType.BooleanConversion] = MappingValueType.Boolean,
            [TransformNodeType.UnitConversion] = MappingValueType.Decimal,
            [TransformNodeType.QuantityRangeAssembly] = MappingValueType.Json,
            [TransformNodeType.RoundingScaling] = MappingValueType.Decimal,
            [TransformNodeType.ValueCodeMapping] = MappingValueType.String,
            [TransformNodeType.CodeableConceptBuilder] = MappingValueType.Json,
            [TransformNodeType.StatusEnumCoercion] = MappingValueType.String,
            [TransformNodeType.ReferenceConstruction] = MappingValueType.String,
            [TransformNodeType.IdentifierFormatting] = MappingValueType.String,
            [TransformNodeType.HumanNameParsing] = null,
            [TransformNodeType.AddressParsing] = null,
            [TransformNodeType.TelecomNormalization] = MappingValueType.String,
            [TransformNodeType.StringNormalization] = MappingValueType.String,
            [TransformNodeType.ConcatenationTemplating] = null,
            [TransformNodeType.ArrayListOperations] = null,
            [TransformNodeType.DefaultNullHandling] = null,
            [TransformNodeType.DateMathAge] = MappingValueType.Integer,
            [TransformNodeType.HashingMasking] = MappingValueType.String,
        };
}

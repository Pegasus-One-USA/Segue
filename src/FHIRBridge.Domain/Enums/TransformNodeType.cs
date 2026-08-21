namespace FHIRBridge.Domain.Enums;

/// <summary>The 20 field-level FHIR-aware transform nodes (FHIRBridge_Top20_Transformations.pdf v1.0).</summary>
public enum TransformNodeType
{
    DateTimeFormat = 1,
    NumberCast = 2,
    BooleanConversion = 3,
    UnitConversion = 4,
    QuantityRangeAssembly = 5,
    RoundingScaling = 6,
    ValueCodeMapping = 7,
    CodeableConceptBuilder = 8,
    StatusEnumCoercion = 9,
    ReferenceConstruction = 10,
    IdentifierFormatting = 11,
    HumanNameParsing = 12,
    AddressParsing = 13,
    TelecomNormalization = 14,
    StringNormalization = 15,
    ConcatenationTemplating = 16,
    ArrayListOperations = 17,
    DefaultNullHandling = 18,
    DateMathAge = 19,
    HashingMasking = 20
}

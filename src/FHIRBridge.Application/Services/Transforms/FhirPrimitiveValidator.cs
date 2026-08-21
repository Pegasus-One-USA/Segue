using System.Text.RegularExpressions;

namespace FHIRBridge.Application.Services.Transforms;

/// <summary>
/// Regex checks against the FHIR R4 primitive-type grammar — the "output validation" contract every node
/// is supposed to honor (§1 of the Top-20 Transformations spec: "a bad transform fails loudly instead of
/// producing invalid FHIR"). Wired into the nodes whose output IS one of these primitives directly
/// (DateTimeFormat, NumberCast's decimal string, BooleanConversion); nodes that build a complex type
/// (HumanName, Address, etc.) validate their own sub-parts inline instead of through this helper.
/// </summary>
public static class FhirPrimitiveValidator
{
    private static readonly Regex DatePattern = new(
        @"^\d{4}(-\d{2}(-\d{2})?)?$", RegexOptions.Compiled);

    private static readonly Regex DateTimePattern = new(
        @"^\d{4}(-\d{2}(-\d{2}(T\d{2}:\d{2}:\d{2}(\.\d+)?(Z|[+-]\d{2}:\d{2}))?)?)?$", RegexOptions.Compiled);

    private static readonly Regex InstantPattern = new(
        @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d+)?(Z|[+-]\d{2}:\d{2})$", RegexOptions.Compiled);

    private static readonly Regex DecimalPattern = new(
        @"^-?\d+(\.\d+)?$", RegexOptions.Compiled);

    public static bool IsValidDate(string value) => DatePattern.IsMatch(value);

    public static bool IsValidDateTime(string value) => DateTimePattern.IsMatch(value);

    public static bool IsValidInstant(string value) => InstantPattern.IsMatch(value);

    public static bool IsValidDecimalString(string value) => DecimalPattern.IsMatch(value);
}

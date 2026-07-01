using FHIRBridge.Application.DTOs;

namespace FHIRBridge.Infrastructure.Terminology;

public sealed class LocalTerminologyLookupService
{
    private static readonly IReadOnlyDictionary<string, string> UcumDisplays =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["%"] = "percent",
            ["/min"] = "per minute",
            ["10*3/uL"] = "thousand per microliter",
            ["10*6/uL"] = "million per microliter",
            ["Cel"] = "degree Celsius",
            ["cm"] = "centimeter",
            ["d"] = "day",
            ["g"] = "gram",
            ["g/dL"] = "gram per deciliter",
            ["h"] = "hour",
            ["kg"] = "kilogram",
            ["L"] = "liter",
            ["m"] = "meter",
            ["mg"] = "milligram",
            ["mg/dL"] = "milligram per deciliter",
            ["mg/L"] = "milligram per liter",
            ["min"] = "minute",
            ["mL"] = "milliliter",
            ["mm[Hg]"] = "millimeter of mercury",
            ["mmol/L"] = "millimole per liter",
            ["ng/mL"] = "nanogram per milliliter",
            ["s"] = "second",
            ["ug/mL"] = "microgram per milliliter"
        };

    private static readonly Dictionary<string, IReadOnlyDictionary<string, string>> CodeSystems =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["http://unitsofmeasure.org"] = UcumDisplays,
            ["https://unitsofmeasure.org"] = UcumDisplays,
            ["urn:oid:2.16.840.1.113883.6.8"] = UcumDisplays
        };

    public Task<TerminologyLookupResult?> LookupAsync(
        string system,
        string code,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(system) || string.IsNullOrWhiteSpace(code))
        {
            return Task.FromResult<TerminologyLookupResult?>(null);
        }

        if (CodeSystems.TryGetValue(system.Trim(), out var values)
            && values.TryGetValue(code.Trim(), out var display))
        {
            return Task.FromResult<TerminologyLookupResult?>(new TerminologyLookupResult(
                system.Trim(),
                code.Trim(),
                display,
                null,
                "Local"));
        }

        return Task.FromResult<TerminologyLookupResult?>(null);
    }
}

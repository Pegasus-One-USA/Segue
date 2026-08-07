using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using FHIRBridge.Domain.Enums;

namespace FHIRBridge.Application.Services.Transforms.Nodes;

/// <summary>1. Date/Time Format Conversion &amp; Normalization.</summary>
public sealed class DateTimeFormatNode : ITransformNode
{
    public TransformNodeType NodeType => TransformNodeType.DateTimeFormat;

    public TransformResult Execute(object? value, IReadOnlyDictionary<string, string> config, string? secret)
    {
        var raw = value?.ToString();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return TransformResult.Ok(null);
        }

        if (!TryParse(raw, out var parsed))
        {
            return TransformResult.Fail($"Unable to parse '{raw}' as a date/time.");
        }

        var targetType = config.Get("targetType", "dateTime");
        return TransformResult.Ok(targetType switch
        {
            "date" => parsed.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            "instant" => parsed.ToString("yyyy-MM-ddTHH:mm:ss.fffzzz", CultureInfo.InvariantCulture),
            _ => parsed.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture)
        });
    }

    private static bool TryParse(string raw, out DateTimeOffset parsed)
    {
        if (long.TryParse(raw, out var epoch))
        {
            // Heuristic: 13+ digits is milliseconds, otherwise seconds.
            parsed = raw.Length >= 13
                ? DateTimeOffset.FromUnixTimeMilliseconds(epoch)
                : DateTimeOffset.FromUnixTimeSeconds(epoch);
            return true;
        }

        // HL7 v2 "YYYYMMDDHHMMSS[.S][+/-ZZZZ]"
        if (Regex.IsMatch(raw, @"^\d{8,14}(\.\d+)?([+-]\d{4})?$"))
        {
            var normalized = Regex.Replace(raw, @"^(\d{4})(\d{2})(\d{2})(\d{2})?(\d{2})?(\d{2})?", "$1-$2-$3T$4:$5:$6");
            if (DateTimeOffset.TryParse(normalized, CultureInfo.InvariantCulture, DateTimeStyles.None, out parsed))
            {
                return true;
            }
        }

        return DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out parsed) ||
               DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out parsed);
    }
}

/// <summary>2. String ↔ Number Casting.</summary>
public sealed class NumberCastNode : ITransformNode
{
    private static readonly Regex StripPattern = new(@"[^\d.\-eE]", RegexOptions.Compiled);

    public TransformNodeType NodeType => TransformNodeType.NumberCast;

    public TransformResult Execute(object? value, IReadOnlyDictionary<string, string> config, string? secret)
    {
        var raw = value?.ToString();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return TransformResult.Ok(null);
        }

        var cleaned = StripPattern.Replace(raw, string.Empty);
        if (!decimal.TryParse(cleaned, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
        {
            return TransformResult.Fail($"'{raw}' is not numeric.");
        }

        var targetType = config.Get("targetType", "decimal");
        return TransformResult.Ok(targetType == "integer" ? Math.Round(parsed, 0, MidpointRounding.AwayFromZero) : parsed);
    }
}

/// <summary>3. Boolean / Flag Conversion.</summary>
public sealed class BooleanConversionNode : ITransformNode
{
    public TransformNodeType NodeType => TransformNodeType.BooleanConversion;

    public TransformResult Execute(object? value, IReadOnlyDictionary<string, string> config, string? secret)
    {
        var raw = value?.ToString();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return TransformResult.Ok(null);
        }

        var trueValues = config.Get("trueValues", "y,yes,1,t,true,+").Split(',', StringSplitOptions.TrimEntries);
        var falseValues = config.Get("falseValues", "n,no,0,f,false,-").Split(',', StringSplitOptions.TrimEntries);

        if (trueValues.Any(t => string.Equals(t, raw, StringComparison.OrdinalIgnoreCase)))
        {
            return TransformResult.Ok(true);
        }

        if (falseValues.Any(f => string.Equals(f, raw, StringComparison.OrdinalIgnoreCase)))
        {
            return TransformResult.Ok(false);
        }

        return TransformResult.Ok(null);
    }
}

/// <summary>4. Unit Conversion (UCUM) — a fixed conversion table covering the spec's worked examples, not a
/// full UCUM engine (per the PDF: "library suggestions are examples, not mandates").</summary>
public sealed class UnitConversionNode : ITransformNode
{
    private static readonly IReadOnlyDictionary<(string From, string To), Func<decimal, decimal>> Conversions =
        new Dictionary<(string, string), Func<decimal, decimal>>
        {
            [("lb_av", "kg")] = v => v * 0.45359237m,
            [("kg", "lb_av")] = v => v / 0.45359237m,
            [("[in_i]", "cm")] = v => v * 2.54m,
            [("cm", "[in_i]")] = v => v / 2.54m,
            [("[degF]", "Cel")] = v => (v - 32m) / 1.8m,
            [("Cel", "[degF]")] = v => v * 1.8m + 32m
        };

    public TransformNodeType NodeType => TransformNodeType.UnitConversion;

    public TransformResult Execute(object? value, IReadOnlyDictionary<string, string> config, string? secret)
    {
        var raw = value?.ToString();
        if (string.IsNullOrWhiteSpace(raw) || !decimal.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var input))
        {
            return TransformResult.Ok(null);
        }

        var sourceUnit = config.Get("sourceUnit");
        var targetUnit = config.Get("targetUnit");
        var factorOverride = config.GetOrNull("factor");

        decimal converted;
        if (factorOverride is not null && decimal.TryParse(factorOverride, NumberStyles.Float, CultureInfo.InvariantCulture, out var factor))
        {
            // Analyte-specific mg/dL <-> mmol/L conversions supply their own molar-mass factor via config.
            converted = config.Get("direction", "multiply") == "divide" ? input / factor : input * factor;
        }
        else if (Conversions.TryGetValue((sourceUnit, targetUnit), out var convert))
        {
            converted = convert(input);
        }
        else
        {
            return TransformResult.Fail($"No known conversion from '{sourceUnit}' to '{targetUnit}'.");
        }

        var precision = config.GetInt("precision", 1);
        converted = Math.Round(converted, precision, MidpointRounding.AwayFromZero);

        var quantity = new JsonObject
        {
            ["value"] = converted,
            ["unit"] = targetUnit,
            ["code"] = config.Get("targetCode", targetUnit),
            ["system"] = "http://unitsofmeasure.org"
        };
        return TransformResult.Ok(quantity);
    }
}

/// <summary>5. Quantity / Range Assembly.</summary>
public sealed class QuantityRangeAssemblyNode : ITransformNode
{
    private static readonly Regex ComparatorPattern = new(@"^(<=|>=|<|>)?\s*(-?\d+(?:\.\d+)?)$", RegexOptions.Compiled);
    private static readonly Regex RangePattern = new(@"^(-?\d+(?:\.\d+)?)\s*-\s*(-?\d+(?:\.\d+)?)$", RegexOptions.Compiled);

    public TransformNodeType NodeType => TransformNodeType.QuantityRangeAssembly;

    public TransformResult Execute(object? value, IReadOnlyDictionary<string, string> config, string? secret)
    {
        var raw = value?.ToString()?.Trim();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return TransformResult.Ok(null);
        }

        var unit = config.Get("unit");

        var rangeMatch = RangePattern.Match(raw);
        if (rangeMatch.Success)
        {
            return TransformResult.Ok(new JsonObject
            {
                ["low"] = new JsonObject { ["value"] = decimal.Parse(rangeMatch.Groups[1].Value, CultureInfo.InvariantCulture), ["unit"] = unit },
                ["high"] = new JsonObject { ["value"] = decimal.Parse(rangeMatch.Groups[2].Value, CultureInfo.InvariantCulture), ["unit"] = unit }
            });
        }

        var comparatorMatch = ComparatorPattern.Match(raw);
        if (comparatorMatch.Success)
        {
            var result = new JsonObject
            {
                ["value"] = decimal.Parse(comparatorMatch.Groups[2].Value, CultureInfo.InvariantCulture),
                ["unit"] = unit
            };
            if (comparatorMatch.Groups[1].Success)
            {
                result["comparator"] = comparatorMatch.Groups[1].Value;
            }

            return TransformResult.Ok(result);
        }

        // Non-numeric ("positive", "trace") routes to a plain text CodeableConcept-shaped value instead of a Quantity.
        return TransformResult.Ok(new JsonObject { ["text"] = raw });
    }
}

/// <summary>6. Rounding / Scaling / Precision.</summary>
public sealed class RoundingScalingNode : ITransformNode
{
    public TransformNodeType NodeType => TransformNodeType.RoundingScaling;

    public TransformResult Execute(object? value, IReadOnlyDictionary<string, string> config, string? secret)
    {
        var raw = value?.ToString();
        if (string.IsNullOrWhiteSpace(raw) || !decimal.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var input))
        {
            return TransformResult.Ok(null);
        }

        var scaleFactor = decimal.TryParse(config.GetOrNull("scaleFactor"), NumberStyles.Float, CultureInfo.InvariantCulture, out var scale)
            ? scale
            : 1m;
        var scaled = input * scaleFactor;

        var places = config.GetInt("decimalPlaces", 2);
        var rounded = Math.Round(scaled, places, MidpointRounding.AwayFromZero);

        if (decimal.TryParse(config.GetOrNull("clampMin"), NumberStyles.Float, CultureInfo.InvariantCulture, out var min))
        {
            rounded = Math.Max(rounded, min);
        }

        if (decimal.TryParse(config.GetOrNull("clampMax"), NumberStyles.Float, CultureInfo.InvariantCulture, out var max))
        {
            rounded = Math.Min(rounded, max);
        }

        return TransformResult.Ok(rounded);
    }
}

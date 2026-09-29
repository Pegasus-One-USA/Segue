using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using FHIRBridge.Domain.Enums;
using UnitsNet;
using UnitsNet.Units;

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

        // A bare year ("2020") or year-month ("2020-05") is valid FHIR `date` precision on its own — parsing it
        // via DateTimeOffset.TryParse would fabricate day=1 (and month=1 for a bare year), which the spec
        // explicitly forbids. Emit the FHIR partial-date form directly instead of ever reaching TryParse.
        if (Regex.IsMatch(raw, @"^\d{4}(-\d{2})?$"))
        {
            if (config.Get("targetType", "dateTime") != "date" && !config.GetBool("allowPartialDate", false))
            {
                return TransformResult.Fail($"'{raw}' has only year/year-month precision — set target type to 'date' or enable allowPartialDate.");
            }

            return TransformResult.Ok(raw);
        }

        if (!TryParse(raw, out var parsed))
        {
            return TransformResult.Fail($"Unable to parse '{raw}' as a date/time.");
        }

        var targetType = config.Get("targetType", "dateTime");
        var formatted = targetType switch
        {
            "date" => parsed.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            "instant" => parsed.ToString("yyyy-MM-ddTHH:mm:ss.fffzzz", CultureInfo.InvariantCulture),
            _ => parsed.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture)
        };

        var valid = targetType switch
        {
            "date" => FhirPrimitiveValidator.IsValidDate(formatted),
            "instant" => FhirPrimitiveValidator.IsValidInstant(formatted),
            _ => FhirPrimitiveValidator.IsValidDateTime(formatted)
        };
        if (!valid)
        {
            return TransformResult.Fail($"Formatted value '{formatted}' failed FHIR {targetType} validation.");
        }

        return TransformResult.Ok(formatted);
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

        // Locale decimal-comma ("1234,50" meaning 1234.50): swap comma/dot BEFORE stripping, so the decimal
        // marker survives and the thousands separator (now a dot) gets stripped along with everything else.
        var normalized = config.Get("decimalSeparator", "dot") == "comma"
            ? raw.Replace(".", string.Empty).Replace(',', '.')
            : raw;

        var cleaned = StripPattern.Replace(normalized, string.Empty);
        if (!decimal.TryParse(cleaned, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
        {
            return TransformResult.Fail($"'{raw}' is not numeric.");
        }

        var targetType = config.Get("targetType", "decimal");
        if (targetType != "integer")
        {
            return FhirPrimitiveValidator.IsValidDecimalString(parsed.ToString(CultureInfo.InvariantCulture))
                ? TransformResult.Ok(parsed)
                : TransformResult.Fail($"'{parsed}' failed FHIR decimal validation.");
        }

        // Rounding alone (the previous behavior) leaves the value as a decimal with zero fractional digits —
        // still typed as decimal, so a destination that respects the CLR type (or a NUMERIC/DECIMAL column)
        // renders it as "2.00" instead of a true integer "2". Cast to a real integral type instead.
        var rounded = Math.Round(parsed, 0, MidpointRounding.AwayFromZero);
        var intResult = (long)rounded;
        return FhirPrimitiveValidator.IsValidDecimalString(intResult.ToString(CultureInfo.InvariantCulture))
            ? TransformResult.Ok(intResult)
            : TransformResult.Fail($"'{intResult}' failed FHIR decimal validation.");
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

/// <summary>4. Unit Conversion (UCUM) — dimensional conversions run through UnitsNet, mapped from UCUM codes
/// to its unit enums; analyte-specific conversions (mg/dL &lt;-&gt; mmol/L, which need a molar-mass factor
/// UnitsNet has no notion of) still go through the manual `factor`/`direction` override.</summary>
public sealed class UnitConversionNode : ITransformNode
{
    // UCUM code -> UnitsNet unit enum, covering the clinical dimensions this pipeline sees most (weight,
    // length, temperature, volume, pressure). Not exhaustive UCUM coverage — extend as new units show up.
    private static readonly IReadOnlyDictionary<string, Enum> UcumUnitMap = new Dictionary<string, Enum>
    {
        ["kg"] = MassUnit.Kilogram,
        ["g"] = MassUnit.Gram,
        ["lb_av"] = MassUnit.Pound,
        ["[lb_av]"] = MassUnit.Pound,
        ["[oz_av]"] = MassUnit.Ounce,
        ["m"] = LengthUnit.Meter,
        ["cm"] = LengthUnit.Centimeter,
        ["mm"] = LengthUnit.Millimeter,
        ["[in_i]"] = LengthUnit.Inch,
        ["[ft_i]"] = LengthUnit.Foot,
        ["Cel"] = TemperatureUnit.DegreeCelsius,
        ["[degF]"] = TemperatureUnit.DegreeFahrenheit,
        ["K"] = TemperatureUnit.Kelvin,
        ["L"] = VolumeUnit.Liter,
        ["mL"] = VolumeUnit.Milliliter,
        ["dL"] = VolumeUnit.Deciliter,
        ["[foz_us]"] = VolumeUnit.UsOunce,
        ["mm[Hg]"] = PressureUnit.MillimeterOfMercury,
        ["kPa"] = PressureUnit.Kilopascal
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
        else if (UcumUnitMap.TryGetValue(sourceUnit, out var sourceEnum) && UcumUnitMap.TryGetValue(targetUnit, out var targetEnum))
        {
            try
            {
                converted = (decimal)Quantity.From((double)input, sourceEnum).As(targetEnum);
            }
            catch (ArgumentException)
            {
                // UnitsNet throws when the two units belong to different quantity kinds (e.g. mass vs length).
                return TransformResult.Fail($"'{sourceUnit}' and '{targetUnit}' are not dimensionally compatible.");
            }
        }
        else
        {
            return TransformResult.Fail($"No known conversion from '{sourceUnit}' to '{targetUnit}'.");
        }

        var precision = config.GetInt("precision", 1);
        converted = Math.Round(converted, precision, MidpointRounding.AwayFromZero);

        // Automatic, destination-aware output shape: a FHIR-native destination (Aidbox/Medplum/Azure Health
        // Data Services) gets the real Quantity structure with unit/code/system attached; a flat destination
        // (SQL, Csv, Mongo, ...) gets just the number — a Quantity object would otherwise land as a literal
        // JSON string in a column meant to hold a plain decimal. Absent destination info (e.g. a direct unit
        // test calling this node without the caller-populated reserved key) defaults to the full structure,
        // matching this node's original, still-documented behavior.
        var destinationTypeName = config.GetOrNull(ReservedTransformConfigKeys.DestinationType);
        var isFhirNativeDestination = destinationTypeName is null || destinationTypeName == DestinationType.FhirRepository.ToString();
        if (!isFhirNativeDestination)
        {
            return TransformResult.Ok(converted);
        }

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
        // The unit is whatever the RULE configures, and only when it configures nothing does the source's own
        // unit stand in (see ReservedTransformConfigKeys.SourceUnitHint). Without that fallback a rule left on
        // its default settings emits "unit": "" for an Observation.valueQuantity whose source JSON plainly
        // carried "mg/dL" — the value survives the hop and its unit silently does not, which for a lab result
        // is the difference between a number and a measurement.
        //
        // Kept SEPARATE from the hint rather than collapsed into one value, because the two rank differently
        // against an incoming Quantity element — see BuildQuantity.
        var configuredUnit = config.Get("unit");
        var sourceUnitHint = config.Get(ReservedTransformConfigKeys.SourceUnitHint);

        // A source field pointed at the whole Quantity element (".valueQuantity" rather than
        // ".valueQuantity.value") arrives as an object, not a scalar — read its parts directly instead of
        // ToString()-ing it into JSON text that neither regex below can match, which would otherwise fall all
        // the way through to the non-numeric "text" branch and stash the raw JSON in it.
        if (TryReadQuantityObject(value) is { } quantityObject)
        {
            return TransformResult.Ok(BuildQuantity(quantityObject, configuredUnit, sourceUnitHint));
        }

        // Scalar input carries no unit of its own, so the hint is the only thing standing between it and
        // "unit": "" — unchanged from before.
        var unit = string.IsNullOrWhiteSpace(configuredUnit) ? sourceUnitHint : configuredUnit;

        var raw = value?.ToString()?.Trim();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return TransformResult.Ok(null);
        }

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

    /// <summary>Recognizes an input that is already a FHIR Quantity element — a JsonObject straight from the
    /// reader, or JSON text carrying one — and returns it. Anything without a "value" property is not a
    /// Quantity and falls through to the scalar parsing above.</summary>
    private static JsonObject? TryReadQuantityObject(object? value)
    {
        var node = value as JsonNode;
        if (node is null && value is string text
            && text.AsSpan().TrimStart() is { Length: > 0 } trimmed && trimmed[0] == '{')
        {
            try
            {
                node = JsonNode.Parse(text);
            }
            catch (System.Text.Json.JsonException)
            {
                return null;
            }
        }

        return node is JsonObject obj && obj.ContainsKey("value") ? obj : null;
    }

    /// <summary>Rebuilds a Quantity from a source element, keeping the rule's configured unit when it has one
    /// and otherwise the element's own "unit" (then its UCUM "code"), plus any comparator it already carried.</summary>
    /// <summary>
    /// Rebuilds a Quantity from an element that already IS one, choosing its unit by three-way precedence:
    /// the rule's own configured unit, then the ELEMENT's own unit/code, then the source-unit hint.
    /// </summary>
    /// <remarks>
    /// The element outranking the hint is the whole point of the ordering. The hint is read once, from the raw
    /// source resource, and handed to this rule wherever it sits in the chain — so by the time a node upstream
    /// has rewritten the value, the hint describes a measurement that no longer exists. A chain of
    /// UnitConversion(mg/dL → mmol/L) followed by this node stamped the converted 4.85 with the hint's
    /// "mg/dL": a number off by a factor of ~38, wearing a label that makes it look right. Before the hint
    /// existed the same chain emitted "unit": "" — visibly missing rather than quietly wrong, which for a lab
    /// result is the better failure.
    ///
    /// An incoming element carries the unit of the value as it stands NOW, which is exactly what the hint
    /// cannot know, so it is the better authority whenever both are present. Nothing is lost when this node
    /// runs first: the element and the hint are then read from the same source element and agree. The hint
    /// still matters for the scalar case (a rule on ".valueQuantity.value" receives a bare number that carries
    /// no unit at all), which is what it was added for. This also keeps emitsTheSourcesOwnUnit below honest —
    /// fed a stale hint it saw a mismatch against the element's real unit and stripped a perfectly correct
    /// system/code, the precise non-computable Quantity it exists to prevent.
    /// </remarks>
    private static JsonObject BuildQuantity(JsonObject source, string configuredUnit, string sourceUnitHint)
    {
        var unit = configuredUnit;
        if (string.IsNullOrWhiteSpace(unit))
        {
            unit = ReadString(source, "unit") ?? ReadString(source, "code") ?? sourceUnitHint ?? string.Empty;
        }

        var quantity = new JsonObject
        {
            ["value"] = source["value"]?.DeepClone(),
            ["unit"] = unit
        };

        // Carry the source's own UCUM coding through. Rebuilding a Quantity from value+unit alone strips the
        // machine-readable half of it, and with a FhirWriteBackJsonPath pointed at the element the patcher
        // replaces it wholesale — so a FHIR-native destination would receive a measurement that can no longer
        // be unit-converted or compared programmatically, only read. UnitConversionNode emits both for the
        // same reason.
        //
        // ONLY when the unit being emitted is still the source's own, though. This node assembles a Quantity,
        // it does not convert one (that is UnitConversionNode) — so a rule that overrides the unit changes the
        // LABEL while `value` stays exactly as the source sent it. Copying the coding anyway produced a
        // self-contradictory Quantity: unit "mmol/L" beside code "mg/dL", asserting in machine-readable form
        // the very thing the human-readable form denies. That is worse than emitting no coding, because the
        // consumer this preservation exists FOR — one doing conversion or comparison off `code` — is the one
        // it misleads. An overridden unit therefore drops both, leaving a plain value+unit the consumer can
        // read but knows better than to compute with.
        var emitsTheSourcesOwnUnit =
            IsSameUnit(unit, ReadString(source, "unit")) || IsSameUnit(unit, ReadString(source, "code"));

        if (emitsTheSourcesOwnUnit)
        {
            if (ReadString(source, "system") is { Length: > 0 } system)
            {
                quantity["system"] = system;
            }

            if (ReadString(source, "code") is { Length: > 0 } code)
            {
                quantity["code"] = code;
            }
        }

        // Unconditional, unlike system/code above: a comparator ("<", ">=") qualifies the VALUE, which this
        // node never alters, so overriding the unit does not make it any less true.
        if (ReadString(source, "comparator") is { Length: > 0 } comparator)
        {
            quantity["comparator"] = comparator;
        }

        return quantity;
    }

    /// <summary>Whether the unit about to be emitted is the same one the source already carried. Trimmed and
    /// case-insensitive so a rule that retypes the source's own unit — "MG/DL", or with a stray space — is
    /// still recognised as agreeing with it and keeps its coding, rather than being punished for restating
    /// what the source said. A genuine synonym ("mg per dL") is not recognised and drops the coding: a
    /// conservative miss (no coding) rather than a misleading one (wrong coding).</summary>
    private static bool IsSameUnit(string unit, string? sourceValue) =>
        sourceValue is not null && string.Equals(unit.Trim(), sourceValue.Trim(), StringComparison.OrdinalIgnoreCase);

    private static string? ReadString(JsonObject source, string propertyName) =>
        source.TryGetPropertyValue(propertyName, out var node) && node is JsonValue value
        && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text)
            ? text
            : null;
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
        var midpoint = config.Get("roundingMode", "halfUp") == "halfEven" ? MidpointRounding.ToEven : MidpointRounding.AwayFromZero;
        var rounded = Math.Round(scaled, places, midpoint);

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

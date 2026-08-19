using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace FHIRBridge.Runtime.Infrastructure.Workflows;

/// <summary>
/// One "Customize fields before writing" rule, parsed from the destination wizard's dest_fhirCustomRules
/// JSON (a <c>Record&lt;resourceType, rule[]&gt;</c> — see destination-wizard.component.ts's FhirCustomRule).
/// Tier 1 only: the 7 simple-parameter transforms (Aidbox-Customize-Transform-UX-Plan.md) — lookup-table-
/// shaped transforms (code mapping, status coercion, CodeableConcept builder, Quantity/Range, array ops) are
/// an explicit follow-up (Tier 2), not handled here.
/// </summary>
public sealed record FhirCustomRule(string Field, string Transform, IReadOnlyDictionary<string, string> Params);

/// <summary>Parses the dest_fhirCustomRules JSON into resource-type-keyed rule lists. Malformed JSON or a
/// malformed individual rule is dropped rather than thrown — a customize-mode misconfiguration should degrade
/// to "fewer transforms applied", never crash the write.</summary>
public static class FhirCustomRulesParser
{
    public static IReadOnlyDictionary<string, IReadOnlyList<FhirCustomRule>> Parse(string? json)
    {
        var empty = new Dictionary<string, IReadOnlyList<FhirCustomRule>>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(json))
        {
            return empty;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return empty;
            }

            var result = new Dictionary<string, IReadOnlyList<FhirCustomRule>>(StringComparer.OrdinalIgnoreCase);
            foreach (var resourceProp in doc.RootElement.EnumerateObject())
            {
                if (resourceProp.Value.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                var rules = new List<FhirCustomRule>();
                foreach (var ruleEl in resourceProp.Value.EnumerateArray())
                {
                    var field = ruleEl.TryGetProperty("field", out var f) && f.ValueKind == JsonValueKind.String ? f.GetString() : null;
                    var transform = ruleEl.TryGetProperty("transform", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
                    if (string.IsNullOrWhiteSpace(field) || string.IsNullOrWhiteSpace(transform))
                    {
                        continue;
                    }

                    var paramsDict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    if (ruleEl.TryGetProperty("params", out var p) && p.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var pp in p.EnumerateObject())
                        {
                            paramsDict[pp.Name] = pp.Value.ValueKind == JsonValueKind.String
                                ? pp.Value.GetString() ?? string.Empty
                                : pp.Value.ToString();
                        }
                    }

                    rules.Add(new FhirCustomRule(field, transform, paramsDict));
                }

                result[resourceProp.Name] = rules;
            }

            return result;
        }
        catch (JsonException)
        {
            return empty;
        }
    }
}

/// <summary>
/// Applies "Customize fields before writing" rules to one resource's raw FHIR JSON, in place, before it's
/// emitted as a MappedDestinationRecord. Each rule is applied independently and failure-isolated — a bad rule
/// (missing field, unparseable date, unsupported unit pair) is recorded as a warning and skipped, never throws
/// out of <see cref="Apply"/> and never blocks the other rules or the resource's write.
/// </summary>
public static class FhirFieldTransformApplier
{
    private static readonly Regex IndexedSegment = new(@"^(?<name>\w+)\[(?<index>\d+)\]$", RegexOptions.Compiled);

    // Simple multiplicative unit pairs (value * factor = converted value). Temperature is handled separately
    // (affine, not multiplicative). Deliberately small and hardcoded — a full UCUM conversion engine is out of
    // scope for Tier 1; unsupported pairs are reported as a rule error rather than silently guessed at.
    private static readonly Dictionary<(string From, string To), double> LinearUnitFactors = new()
    {
        [("lb", "kg")] = 0.45359237,
        [("kg", "lb")] = 2.2046226218,
        [("oz", "g")] = 28.349523125,
        [("g", "oz")] = 0.0352739619,
        [("in", "cm")] = 2.54,
        [("cm", "in")] = 0.3937007874,
        [("ft", "m")] = 0.3048,
        [("m", "ft")] = 3.2808398950,
    };

    public static string Apply(string sourceJson, string resourceType, IReadOnlyList<FhirCustomRule> rules, List<string> ruleErrors)
    {
        if (rules.Count == 0)
        {
            return sourceJson;
        }

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(sourceJson);
        }
        catch (JsonException)
        {
            ruleErrors.Add($"{resourceType}: could not parse resource JSON to apply customize rules — sent unmodified.");
            return sourceJson;
        }

        if (root is not JsonObject)
        {
            return sourceJson;
        }

        foreach (var rule in rules)
        {
            try
            {
                ApplyRule(root, rule);
            }
            catch (Exception ex) when (ex is FormatException or InvalidOperationException or ArgumentException)
            {
                ruleErrors.Add($"{resourceType}.{rule.Field}: {rule.Transform} failed — {ex.Message}");
            }
        }

        // This re-serializes the WHOLE resource (not just rule-touched fields), so the default HTML-safe encoder
        // (which escapes <, >, &, ' as \uXXXX) would cosmetically alter untouched fields too, not just ones this
        // rule set touched. Relaxed escaping keeps output close to the original source JSON's own formatting —
        // this is an API payload sent to Aidbox, not HTML, so there's no XSS concern to encode against here.
        return root.ToJsonString(new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    }

    private static void ApplyRule(JsonNode root, FhirCustomRule rule)
    {
        var (container, key) = ResolveParent(root, rule.Field);
        var current = GetChild(container, key);

        switch (rule.Transform)
        {
            case "coalesce":
                if (current is null || (current is JsonValue cv && cv.ToString().Length == 0))
                {
                    SetChild(container, key, JsonValue.Create(rule.Params.GetValueOrDefault("defaultValue", "")));
                }
                return;

            case "dateShift":
            {
                var raw = RequireString(current, rule.Field);
                if (!DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
                {
                    throw new FormatException($"'{raw}' is not a recognizable date/time value.");
                }
                var days = int.TryParse(rule.Params.GetValueOrDefault("days", "0"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var d) ? d : 0;
                SetChild(container, key, JsonValue.Create(parsed.AddDays(days).ToString("O", CultureInfo.InvariantCulture)));
                return;
            }

            case "hashMask":
            {
                var raw = RequireString(current, rule.Field);
                if (rule.Params.GetValueOrDefault("mode", "hash") == "mask")
                {
                    var showLastN = int.TryParse(rule.Params.GetValueOrDefault("showLastN", "0"), out var n) ? Math.Max(0, n) : 0;
                    var visible = showLastN >= raw.Length ? raw : raw[^showLastN..];
                    var masked = new string('*', Math.Max(0, raw.Length - visible.Length)) + visible;
                    SetChild(container, key, JsonValue.Create(masked));
                }
                else
                {
                    var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
                    SetChild(container, key, JsonValue.Create(hash));
                }
                return;
            }

            case "dateFormat":
            {
                var raw = RequireString(current, rule.Field);
                if (!DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
                {
                    throw new FormatException($"'{raw}' is not a recognizable date/time value.");
                }

                var targetFormat = rule.Params.GetValueOrDefault("targetFormat", "iso8601");
                var formatted = targetFormat == "custom" && rule.Params.TryGetValue("pattern", out var pattern) && pattern.Length > 0
                    ? parsed.ToString(pattern, CultureInfo.InvariantCulture)
                    : parsed.ToString("O", CultureInfo.InvariantCulture);
                SetChild(container, key, JsonValue.Create(formatted));
                return;
            }

            case "typeCast":
            {
                var raw = RequireString(current, rule.Field);
                var targetType = rule.Params.GetValueOrDefault("targetType", "string");
                JsonNode cast = targetType switch
                {
                    "integer" when int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) => JsonValue.Create(i),
                    "integer" => throw new FormatException($"'{raw}' is not a valid integer."),
                    "decimal" when decimal.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => JsonValue.Create(d),
                    "decimal" => throw new FormatException($"'{raw}' is not a valid decimal."),
                    _ => JsonValue.Create(raw),
                };
                SetChild(container, key, cast);
                return;
            }

            case "booleanConversion":
            {
                // Missing/empty is a legitimate "false" here (e.g. an unset deceasedBoolean-style flag) — unlike
                // the other transforms below, an absent value isn't an error condition for this one.
                var raw = GetStringValue(current) ?? "";
                var trueTokens = (rule.Params.GetValueOrDefault("trueValues", "") ?? "")
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var isTrue = trueTokens.Any(t => string.Equals(t, raw, StringComparison.OrdinalIgnoreCase));
                SetChild(container, key, JsonValue.Create(isTrue));
                return;
            }

            case "unitConversion":
            {
                var raw = RequireString(current, rule.Field);
                if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                {
                    throw new FormatException($"'{raw}' is not a numeric value to convert.");
                }

                var fromUnit = rule.Params.GetValueOrDefault("fromUnit", "");
                var toUnit = rule.Params.GetValueOrDefault("toUnit", "");
                var converted = ConvertUnit(value, fromUnit, toUnit);
                SetChild(container, key, JsonValue.Create(Math.Round(converted, 4)));
                return;
            }

            case "quantityRange":
            {
                var raw = RequireString(current, rule.Field);
                var unit = rule.Params.GetValueOrDefault("unit", "");

                if (rule.Params.GetValueOrDefault("mode", "quantity") == "range")
                {
                    var delimiter = rule.Params.GetValueOrDefault("delimiter", "-");
                    var parts = raw.Split(delimiter, 2, StringSplitOptions.TrimEntries);
                    if (parts.Length != 2
                        || !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var low)
                        || !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var high))
                    {
                        throw new FormatException($"'{raw}' could not be split into a low/high range on '{delimiter}'.");
                    }
                    SetChild(container, key, new JsonObject
                    {
                        ["low"] = new JsonObject { ["value"] = low, ["unit"] = unit },
                        ["high"] = new JsonObject { ["value"] = high, ["unit"] = unit },
                    });
                }
                else
                {
                    var (comparator, numberPart) = SplitComparator(raw);
                    if (!double.TryParse(numberPart, NumberStyles.Float, CultureInfo.InvariantCulture, out var qty))
                    {
                        throw new FormatException($"'{raw}' is not a numeric quantity value.");
                    }
                    var quantity = new JsonObject
                    {
                        ["value"] = qty,
                        ["unit"] = unit,
                        ["system"] = "http://unitsofmeasure.org",
                        ["code"] = unit,
                    };
                    if (comparator is not null)
                    {
                        quantity["comparator"] = comparator;
                    }
                    SetChild(container, key, quantity);
                }
                return;
            }

            case "roundPrecision":
            {
                var raw = RequireString(current, rule.Field);
                if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                {
                    throw new FormatException($"'{raw}' is not numeric.");
                }
                var decimals = int.TryParse(rule.Params.GetValueOrDefault("decimals", "2"), out var d) ? d : 2;
                SetChild(container, key, JsonValue.Create(Math.Round(value, decimals)));
                return;
            }

            case "codeLookup":
            case "statusCoercion":
            {
                var raw = RequireString(current, rule.Field);
                var pairs = ParseLookupPairs(rule.Params.GetValueOrDefault("pairs", "[]"));
                var match = pairs.FirstOrDefault(p => string.Equals(p.From, raw, StringComparison.OrdinalIgnoreCase));
                if (match is null)
                {
                    throw new InvalidOperationException($"no lookup mapping configured for value '{raw}'.");
                }
                SetChild(container, key, JsonValue.Create(match.To));
                return;
            }

            case "codeableConcept":
            {
                var raw = RequireString(current, rule.Field);
                var system = rule.Params.GetValueOrDefault("system", "");
                var display = rule.Params.GetValueOrDefault("display", "");
                var coding = new JsonObject { ["system"] = system, ["code"] = raw };
                if (!string.IsNullOrWhiteSpace(display))
                {
                    coding["display"] = display;
                }
                SetChild(container, key, new JsonObject { ["coding"] = new JsonArray(coding) });
                return;
            }

            case "identifierFormat":
            {
                var raw = RequireString(current, rule.Field);
                var system = rule.Params.GetValueOrDefault("system", "");
                SetChild(container, key, new JsonObject { ["system"] = system, ["value"] = raw });
                return;
            }

            case "humanNameFormat":
            {
                var raw = RequireString(current, rule.Field);
                var delimiter = rule.Params.GetValueOrDefault("delimiter", ", ");
                var parts = raw.Split(delimiter, 2, StringSplitOptions.TrimEntries);
                var name = new JsonObject();
                if (parts.Length == 2)
                {
                    name["family"] = parts[0];
                    name["given"] = new JsonArray(JsonValue.Create(parts[1]));
                }
                else
                {
                    name["family"] = raw;
                }
                SetChild(container, key, name);
                return;
            }

            case "addressParse":
            {
                var raw = RequireString(current, rule.Field);
                var delimiter = rule.Params.GetValueOrDefault("delimiter", ",");
                var parts = raw.Split(delimiter).Select(p => p.Trim()).ToArray();
                var address = new JsonObject { ["line"] = new JsonArray(parts.Length > 0 ? JsonValue.Create(parts[0]) : JsonValue.Create("")) };
                if (parts.Length > 1) address["city"] = parts[1];
                if (parts.Length > 2) address["state"] = parts[2];
                if (parts.Length > 3) address["postalCode"] = parts[3];
                SetChild(container, key, address);
                return;
            }

            case "stringClean":
            {
                var raw = RequireString(current, rule.Field);
                // Trim is applied in every mode — untrimmed-but-cased text isn't meaningfully "cleaned."
                var cleaned = rule.Params.GetValueOrDefault("mode", "trim") switch
                {
                    "upper" => raw.Trim().ToUpperInvariant(),
                    "lower" => raw.Trim().ToLowerInvariant(),
                    "collapseWhitespace" => Regex.Replace(raw.Trim(), @"\s+", " "),
                    _ => raw.Trim(),
                };
                SetChild(container, key, JsonValue.Create(cleaned));
                return;
            }

            case "stringTemplate":
            {
                var raw = RequireString(current, rule.Field);
                if (rule.Params.GetValueOrDefault("mode", "template") == "split")
                {
                    var delimiter = rule.Params.GetValueOrDefault("delimiter", ",");
                    var arr = new JsonArray(raw.Split(delimiter).Select(p => (JsonNode?)JsonValue.Create(p.Trim())).ToArray());
                    SetChild(container, key, arr);
                }
                else
                {
                    var template = rule.Params.GetValueOrDefault("template", "{value}");
                    SetChild(container, key, JsonValue.Create(template.Replace("{value}", raw)));
                }
                return;
            }

            case "arrayOp":
            {
                if (current is not JsonArray currentArray)
                {
                    throw new InvalidOperationException($"field '{rule.Field}' is not an array.");
                }

                switch (rule.Params.GetValueOrDefault("operation", "first"))
                {
                    case "first":
                        if (currentArray.Count == 0)
                        {
                            throw new InvalidOperationException("array is empty.");
                        }
                        SetChild(container, key, currentArray[0]?.DeepClone());
                        break;

                    case "filterBySystem":
                    {
                        var filterSystem = rule.Params.GetValueOrDefault("filterSystem", "");
                        var found = currentArray.FirstOrDefault(el =>
                            string.Equals(GetStringValue((el as JsonObject)?["system"]), filterSystem, StringComparison.OrdinalIgnoreCase));
                        if (found is null)
                        {
                            throw new InvalidOperationException($"no array entry with system '{filterSystem}'.");
                        }
                        SetChild(container, key, found.DeepClone());
                        break;
                    }

                    case "join":
                    {
                        var joinDelimiter = rule.Params.GetValueOrDefault("joinDelimiter", ", ");
                        var joined = string.Join(joinDelimiter, currentArray.Select(el => GetStringValue(el) ?? ""));
                        SetChild(container, key, JsonValue.Create(joined));
                        break;
                    }

                    default:
                        throw new InvalidOperationException($"unknown array operation '{rule.Params.GetValueOrDefault("operation", "")}'.");
                }
                return;
            }

            case "telecom":
            {
                var raw = RequireString(current, rule.Field);
                var telecom = new JsonObject
                {
                    ["system"] = rule.Params.GetValueOrDefault("system", "phone"),
                    ["value"] = raw,
                };
                if (rule.Params.TryGetValue("use", out var use) && !string.IsNullOrWhiteSpace(use))
                {
                    telecom["use"] = use;
                }
                SetChild(container, key, telecom);
                return;
            }

            case "referenceConstruct":
            {
                var raw = RequireString(current, rule.Field);
                var targetResourceType = rule.Params.GetValueOrDefault("targetResourceType", "");
                if (string.IsNullOrWhiteSpace(targetResourceType))
                {
                    throw new InvalidOperationException("no target resource type configured for this reference.");
                }
                SetChild(container, key, new JsonObject { ["reference"] = $"{targetResourceType}/{raw}" });
                return;
            }

            default:
                throw new InvalidOperationException($"unknown transform '{rule.Transform}'.");
        }
    }

    private static double ConvertUnit(double value, string fromUnit, string toUnit)
    {
        if (string.Equals(fromUnit, toUnit, StringComparison.OrdinalIgnoreCase))
        {
            return value;
        }

        if (string.Equals(fromUnit, "F", StringComparison.OrdinalIgnoreCase) && string.Equals(toUnit, "C", StringComparison.OrdinalIgnoreCase))
        {
            return (value - 32) * 5 / 9;
        }
        if (string.Equals(fromUnit, "C", StringComparison.OrdinalIgnoreCase) && string.Equals(toUnit, "F", StringComparison.OrdinalIgnoreCase))
        {
            return value * 9 / 5 + 32;
        }

        if (LinearUnitFactors.TryGetValue((fromUnit, toUnit), out var factor))
        {
            return value * factor;
        }

        throw new InvalidOperationException($"unsupported unit conversion '{fromUnit}' → '{toUnit}'.");
    }

    /// <summary>Splits a leading FHIR Quantity comparator (&lt;, &lt;=, &gt;, &gt;=) off a raw value string,
    /// e.g. "&lt;5" -> ("&lt;", "5"). Returns a null comparator when none is present.</summary>
    private static (string? Comparator, string Number) SplitComparator(string raw)
    {
        foreach (var c in new[] { "<=", ">=", "<", ">" })
        {
            if (raw.StartsWith(c, StringComparison.Ordinal))
            {
                return (c, raw[c.Length..].Trim());
            }
        }
        return (null, raw.Trim());
    }

    private sealed record LookupPair(string From, string To);

    /// <summary>Parses the codeLookup/statusCoercion rule's params['pairs'] JSON (a {from,to}[] array — see
    /// destination-wizard.component.ts's getLookupPairs/FhirLookupPair) into a lookup list. Malformed JSON or a
    /// pair missing "from" is dropped rather than thrown, matching FhirCustomRulesParser's own tolerance.</summary>
    private static List<LookupPair> ParseLookupPairs(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var pairs = new List<LookupPair>();
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                var from = el.TryGetProperty("from", out var f) ? f.GetString() : null;
                var to = el.TryGetProperty("to", out var t) ? t.GetString() : null;
                if (!string.IsNullOrEmpty(from) && to is not null)
                {
                    pairs.Add(new LookupPair(from, to));
                }
            }
            return pairs;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string RequireString(JsonNode? node, string field)
    {
        var value = GetStringValue(node);
        if (value is null || value.Length == 0)
        {
            throw new InvalidOperationException($"field '{field}' is missing or empty.");
        }
        return value;
    }

    private static string? GetStringValue(JsonNode? node)
    {
        if (node is not JsonValue value)
        {
            return null;
        }
        if (value.TryGetValue<string>(out var s)) return s;
        if (value.TryGetValue<bool>(out var b)) return b ? "true" : "false";
        if (value.TryGetValue<double>(out var d)) return d.ToString(CultureInfo.InvariantCulture);
        if (value.TryGetValue<long>(out var l)) return l.ToString(CultureInfo.InvariantCulture);
        return value.ToString();
    }

    /// <summary>Navigates a dotted FHIRPath-lite field path (e.g. "period.start", "identifier[0].system") down
    /// to the parent container (object or array) holding the final segment, plus the key/index to read/write
    /// within it. Does not auto-create missing intermediate objects/arrays — a path that doesn't exist on this
    /// resource throws, which the caller records as a per-rule error rather than a crash.</summary>
    private static (JsonNode Container, object Key) ResolveParent(JsonNode root, string path)
    {
        var segments = path.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            throw new InvalidOperationException("empty field path.");
        }

        JsonNode current = root;
        for (var i = 0; i < segments.Length - 1; i++)
        {
            var next = Step(current, segments[i]);
            current = next ?? throw new InvalidOperationException($"path segment '{segments[i]}' does not exist.");
        }

        var (name, index) = SplitIndex(segments[^1]);
        if (index is null)
        {
            return (current, name);
        }

        var arr = (current as JsonObject)?[name] as JsonArray
            ?? throw new InvalidOperationException($"'{name}' is not an array on this resource.");
        if (index.Value >= arr.Count)
        {
            throw new InvalidOperationException($"'{name}[{index.Value}]' does not exist (only {arr.Count} entr{(arr.Count == 1 ? "y" : "ies")}).");
        }
        return (arr, index.Value);
    }

    private static JsonNode? Step(JsonNode current, string segment)
    {
        var (name, index) = SplitIndex(segment);
        var child = (current as JsonObject)?[name];
        if (index is null)
        {
            return child;
        }
        var arr = child as JsonArray;
        return arr is not null && index.Value < arr.Count ? arr[index.Value] : null;
    }

    private static (string Name, int? Index) SplitIndex(string segment)
    {
        var match = IndexedSegment.Match(segment);
        return match.Success ? (match.Groups["name"].Value, int.Parse(match.Groups["index"].Value, CultureInfo.InvariantCulture)) : (segment, null);
    }

    private static JsonNode? GetChild(JsonNode container, object key) => key switch
    {
        string name => (container as JsonObject)?[name],
        int index => (container as JsonArray)?[index],
        _ => null,
    };

    private static void SetChild(JsonNode container, object key, JsonNode? value)
    {
        switch (key)
        {
            case string name when container is JsonObject obj:
                obj[name] = value;
                break;
            case int index when container is JsonArray arr:
                arr[index] = value;
                break;
        }
    }
}

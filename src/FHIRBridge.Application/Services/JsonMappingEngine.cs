using System.Globalization;
using System.Text.Json;
using FHIRBridge.Application.Abstractions.Mapping;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Domain.Enums;

namespace FHIRBridge.Application.Services;

public sealed class JsonMappingEngine : IJsonMappingEngine
{
    public MappingTestResultDto Map(
        string sourceJson,
        IReadOnlyCollection<MappingFieldDto> fields,
        IReadOnlyDictionary<string, object?>? systemValues = null)
    {
        using var document = JsonDocument.Parse(sourceJson);
        var root = document.RootElement;
        var errors = new List<string>();

        var parent = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        var repeatFields = new List<(string Target, List<object?> Values)>();
        // table name -> ordered rows keyed by index path
        var childTables = new Dictionary<string, Dictionary<string, Dictionary<string, object?>>>(StringComparer.OrdinalIgnoreCase);

        foreach (var field in fields)
        {
            // System-value field: sourced from pipeline/runtime context (run id, write time, resource type, …)
            // rather than the source JSON. The value flows into the row exactly like a normal mapped column.
            if (IsSystemToken(field.JsonPath))
            {
                object? systemValue = null;
                systemValues?.TryGetValue(field.JsonPath, out systemValue);
                if (systemValue is null && field.IsRequired)
                {
                    errors.Add($"Required system field '{field.TargetField}' had no value for token '{field.JsonPath}'.");
                }

                parent[field.TargetField] = systemValue;
                continue;
            }

            var matches = ResolveAll(root, field.JsonPath);
            var policy = field.ArrayPolicy;

            if (matches.Count == 0)
            {
                if (!string.IsNullOrWhiteSpace(field.DefaultValue))
                {
                    parent[field.TargetField] = ConvertValue(
                        field.DefaultValue, field.ValueType, field.Format, field.TargetField, errors,
                        field.MaxLength, field.Precision, field.Scale);
                    continue;
                }

                if (field.IsRequired)
                {
                    errors.Add($"Required field '{field.TargetField}' was not found at path '{field.JsonPath}'.");
                }

                parent[field.TargetField] = null;
                continue;
            }

            var values = matches
                .Select(m => ConvertElement(
                    m.Element, field.ValueType, field.Format, field.TargetField, errors,
                    field.MaxLength, field.Precision, field.Scale))
                .ToList();

            switch (policy)
            {
                case ArrayPolicy.RepeatParent:
                    repeatFields.Add((field.TargetField, values));
                    break;

                case ArrayPolicy.SeparateDestination:
                {
                    var table = ChildTableName(field);
                    if (!childTables.TryGetValue(table, out var rows))
                    {
                        rows = new Dictionary<string, Dictionary<string, object?>>(StringComparer.Ordinal);
                        childTables[table] = rows;
                    }

                    for (var i = 0; i < matches.Count; i++)
                    {
                        var key = string.Join('-', matches[i].Indices);
                        if (!rows.TryGetValue(key, out var row))
                        {
                            row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["RowIndex"] = key };
                            rows[key] = row;
                        }

                        row[field.TargetField] = values[i];
                    }

                    break;
                }

                case ArrayPolicy.StoreJson:
                    parent[field.TargetField] = JsonSerializer.Serialize(values);
                    break;

                case ArrayPolicy.RejectIfMultiple:
                    if (values.Count > 1)
                    {
                        errors.Add($"Field '{field.TargetField}' rejected: {values.Count} values found at '{field.JsonPath}' but only one is allowed.");
                    }

                    parent[field.TargetField] = values[0];
                    break;

                case ArrayPolicy.CorrelateByCode:
                    parent[field.TargetField] = ResolveCorrelatedValue(root, field, matches, values, errors);
                    break;

                case ArrayPolicy.Scalar:
                case ArrayPolicy.FirstItem:
                default:
                    parent[field.TargetField] = values[0];
                    break;
            }
        }

        var rowsList = BuildParentRows(parent, repeatFields);
        var childTableDtos = childTables
            .Select(kv => new MappingChildTableDto(kv.Key, kv.Value.Values.Cast<IReadOnlyDictionary<string, object?>>().ToList()))
            .ToList();

        return new MappingTestResultDto(rowsList[0], errors, rowsList, childTableDtos);
    }

    /// <summary>
    /// Selects the value among <paramref name="matches"/>/<paramref name="values"/> (same order, one per array item)
    /// whose sibling code element — resolved via <see cref="MappingFieldDto.CorrelationCodeJsonPath"/>, sharing the
    /// same outermost array index as <paramref name="matches"/> — equals <see cref="MappingFieldDto.CorrelationCodeValue"/>.
    /// This is how e.g. a blood-pressure Observation's systolic/diastolic <c>component[]</c> entries are told apart:
    /// position alone isn't reliable, but each component carries a LOINC code identifying which reading it is.
    /// </summary>
    private static object? ResolveCorrelatedValue(
        JsonElement root,
        MappingFieldDto field,
        List<(JsonElement Element, IReadOnlyList<int> Indices)> matches,
        List<object?> values,
        List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(field.CorrelationCodeJsonPath) || string.IsNullOrWhiteSpace(field.CorrelationCodeValue))
        {
            errors.Add($"Field '{field.TargetField}' uses CorrelateByCode but is missing CorrelationCodeJsonPath/CorrelationCodeValue.");
            return null;
        }

        var codeMatches = ResolveAll(root, field.CorrelationCodeJsonPath);
        var matchingOuterIndices = codeMatches
            .Where(m => m.Element.ValueKind == JsonValueKind.String &&
                        string.Equals(m.Element.GetString(), field.CorrelationCodeValue, StringComparison.OrdinalIgnoreCase))
            .Where(m => m.Indices.Count > 0)
            .Select(m => m.Indices[0])
            .ToHashSet();

        if (matchingOuterIndices.Count == 0)
        {
            return null;
        }

        for (var i = 0; i < matches.Count; i++)
        {
            if (matches[i].Indices.Count > 0 && matchingOuterIndices.Contains(matches[i].Indices[0]))
            {
                return values[i];
            }
        }

        return null;
    }

    private static List<IReadOnlyDictionary<string, object?>> BuildParentRows(
        Dictionary<string, object?> parent,
        List<(string Target, List<object?> Values)> repeatFields)
    {
        if (repeatFields.Count == 0)
        {
            return [parent];
        }

        var count = repeatFields.Max(r => r.Values.Count);
        if (count == 0)
        {
            return [parent];
        }

        var rows = new List<IReadOnlyDictionary<string, object?>>(count);
        for (var i = 0; i < count; i++)
        {
            var row = new Dictionary<string, object?>(parent, StringComparer.OrdinalIgnoreCase);
            foreach (var (target, values) in repeatFields)
            {
                row[target] = i < values.Count ? values[i] : null;
            }

            rows.Add(row);
        }

        return rows;
    }

    /// <summary>A field is system-sourced when its path is a reserved <c>@token</c> rather than a <c>$</c> JSONPath.</summary>
    private static bool IsSystemToken(string? jsonPath)
        => !string.IsNullOrEmpty(jsonPath) && jsonPath[0] == '@';

    private static string ChildTableName(MappingFieldDto field)
    {
        if (!string.IsNullOrWhiteSpace(field.DestinationObject))
        {
            return field.DestinationObject!;
        }

        var ancestor = field.ArrayAncestors is { Count: > 0 }
            ? field.ArrayAncestors[^1]
            : field.TargetField;

        var pascal = string.Concat(ancestor.Split('.', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => char.ToUpperInvariant(s[0]) + s[1..]));
        return "dbo." + pascal;
    }

    /// <summary>
    /// Resolves a JSONPath into every matching element, supporting `[*]` (fan-out) and `[n]` (fixed index).
    /// Each match carries its array index path so SeparateDestination / RepeatParent can align rows.
    /// </summary>
    private static List<(JsonElement Element, IReadOnlyList<int> Indices)> ResolveAll(JsonElement root, string path)
    {
        var frontier = new List<(JsonElement Element, List<int> Indices)> { (root, []) };

        if (string.IsNullOrWhiteSpace(path) || path[0] != '$')
        {
            return [];
        }

        var segments = path == "$"
            ? Array.Empty<string>()
            : path[2..].Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var segment in segments)
        {
            var (propertyName, indexToken) = ParseSegment(segment);
            var next = new List<(JsonElement, List<int>)>();

            foreach (var (element, indices) in frontier)
            {
                var current = element;

                if (!string.IsNullOrEmpty(propertyName))
                {
                    if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(propertyName, out current))
                    {
                        continue;
                    }
                }

                if (indexToken is null)
                {
                    next.Add((current, indices));
                }
                else if (indexToken == "*")
                {
                    if (current.ValueKind != JsonValueKind.Array)
                    {
                        continue;
                    }

                    var i = 0;
                    foreach (var item in current.EnumerateArray())
                    {
                        next.Add((item, [.. indices, i]));
                        i++;
                    }
                }
                else if (int.TryParse(indexToken, NumberStyles.None, CultureInfo.InvariantCulture, out var index))
                {
                    if (current.ValueKind != JsonValueKind.Array || current.GetArrayLength() <= index)
                    {
                        continue;
                    }

                    next.Add((current.EnumerateArray().ElementAt(index), [.. indices, index]));
                }
            }

            frontier = next;
        }

        return frontier
            .Where(f => f.Element.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
            .Select(f => (f.Element, (IReadOnlyList<int>)f.Indices))
            .ToList();
    }

    private static (string PropertyName, string? IndexToken) ParseSegment(string segment)
    {
        var bracketIndex = segment.IndexOf('[', StringComparison.Ordinal);
        if (bracketIndex < 0)
        {
            return (segment, null);
        }

        var closingBracketIndex = segment.IndexOf(']', bracketIndex);
        if (closingBracketIndex <= bracketIndex)
        {
            return (segment, null);
        }

        var propertyName = segment[..bracketIndex];
        var indexToken = segment[(bracketIndex + 1)..closingBracketIndex];
        return (propertyName, indexToken);
    }

    private static object? ConvertElement(
        JsonElement element,
        MappingValueType valueType,
        string? format,
        string targetField,
        List<string> errors,
        int? maxLength = null,
        int? precision = null,
        int? scale = null)
    {
        if (element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        return valueType switch
        {
            MappingValueType.String => ValidateLength(
                element.ValueKind == JsonValueKind.String ? element.GetString() : element.ToString(),
                maxLength, targetField, errors),
            MappingValueType.Integer => ConvertInteger(element.ToString(), targetField, errors),
            MappingValueType.Decimal => ConvertDecimal(element.ToString(), targetField, errors, precision, scale),
            MappingValueType.Boolean => ConvertBoolean(element.ToString(), targetField, errors),
            MappingValueType.Date => ConvertDate(element.ToString(), format, targetField, errors)?.Date,
            MappingValueType.DateTime => ConvertDate(element.ToString(), format, targetField, errors),
            MappingValueType.Json => element.GetRawText(),
            _ => element.ToString()
        };
    }

    private static object? ConvertValue(
        string value,
        MappingValueType valueType,
        string? format,
        string targetField,
        List<string> errors,
        int? maxLength = null,
        int? precision = null,
        int? scale = null)
    {
        return valueType switch
        {
            MappingValueType.String => ValidateLength(value, maxLength, targetField, errors),
            MappingValueType.Integer => ConvertInteger(value, targetField, errors),
            MappingValueType.Decimal => ConvertDecimal(value, targetField, errors, precision, scale),
            MappingValueType.Boolean => ConvertBoolean(value, targetField, errors),
            MappingValueType.Date => ConvertDate(value, format, targetField, errors)?.Date,
            MappingValueType.DateTime => ConvertDate(value, format, targetField, errors),
            MappingValueType.Json => value,
            _ => value
        };
    }

    /// <summary>Rejects a string value that would exceed the destination column's max length (e.g. an
    /// NVARCHAR(100) receiving a 500-character FHIR display/text value) rather than letting the database truncate
    /// or reject it at write time. Null <paramref name="maxLength"/> (no destination schema known, or a
    /// max-length-less column type) means no check is performed.</summary>
    private static string? ValidateLength(string? value, int? maxLength, string targetField, List<string> errors)
    {
        if (value is not null && maxLength is { } max && value.Length > max)
        {
            errors.Add(
                $"Field '{targetField}' value is {value.Length} characters but the destination column allows at most {max}.");
            return null;
        }

        return value;
    }

    private static object? ConvertInteger(string? value, string targetField, List<string> errors)
    {
        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        errors.Add($"Field '{targetField}' could not be converted.");
        return null;
    }

    private static object? ConvertDecimal(
        string? value, string targetField, List<string> errors, int? precision = null, int? scale = null)
    {
        if (!decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed))
        {
            errors.Add($"Field '{targetField}' could not be converted.");
            return null;
        }

        // Total significant digits allowed by the destination column (e.g. DECIMAL(5,2) allows at most 5 digits
        // total, 2 of them after the decimal point — so at most 3 integer digits). No precision known (non-numeric
        // destination schema, or a type like float/money the driver doesn't report precision for) means no check.
        if (precision is { } p)
        {
            var effectiveScale = scale ?? 0;
            var maxIntegerDigits = Math.Max(p - effectiveScale, 0);
            var rounded = Math.Round(parsed, effectiveScale, MidpointRounding.AwayFromZero);
            var integerPart = Math.Truncate(Math.Abs(rounded));
            var integerDigits = integerPart == 0 ? 1 : (int)Math.Floor(Math.Log10((double)integerPart)) + 1;

            if (integerDigits > maxIntegerDigits)
            {
                errors.Add(
                    $"Field '{targetField}' value {parsed.ToString(CultureInfo.InvariantCulture)} exceeds the " +
                    $"destination column's numeric precision (at most {maxIntegerDigits} integer digit(s), {effectiveScale} decimal place(s)).");
                return null;
            }
        }

        return parsed;
    }

    private static object? ConvertBoolean(string? value, string targetField, List<string> errors)
    {
        if (bool.TryParse(value, out var parsed))
        {
            return parsed;
        }

        errors.Add($"Field '{targetField}' could not be converted.");
        return null;
    }

    private static DateTime? ConvertDate(
        string? value,
        string? format,
        string targetField,
        List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var isParsed = string.IsNullOrWhiteSpace(format)
            ? DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsedDate)
            : DateTime.TryParseExact(value, format, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out parsedDate);

        if (isParsed)
        {
            return parsedDate;
        }

        errors.Add($"Field '{targetField}' could not be converted to a date/time.");
        return null;
    }
}

using System.Globalization;
using System.Text.Json;
using FHIRBridge.Application.Abstractions.Mapping;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Domain.Enums;

namespace FHIRBridge.Application.Services;

public sealed class JsonMappingEngine : IJsonMappingEngine
{
    public MappingTestResultDto Map(string sourceJson, IReadOnlyCollection<MappingFieldDto> fields)
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
            var matches = ResolveAll(root, field.JsonPath);
            var policy = field.ArrayPolicy;

            if (matches.Count == 0)
            {
                if (!string.IsNullOrWhiteSpace(field.DefaultValue))
                {
                    parent[field.TargetField] = ConvertValue(field.DefaultValue, field.ValueType, field.Format, field.TargetField, errors);
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
                .Select(m => ConvertElement(m.Element, field.ValueType, field.Format, field.TargetField, errors))
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
        List<string> errors)
    {
        if (element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        return valueType switch
        {
            MappingValueType.String => element.ValueKind == JsonValueKind.String ? element.GetString() : element.ToString(),
            MappingValueType.Integer => ConvertInteger(element.ToString(), targetField, errors),
            MappingValueType.Decimal => ConvertDecimal(element.ToString(), targetField, errors),
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
        List<string> errors)
    {
        return valueType switch
        {
            MappingValueType.String => value,
            MappingValueType.Integer => ConvertInteger(value, targetField, errors),
            MappingValueType.Decimal => ConvertDecimal(value, targetField, errors),
            MappingValueType.Boolean => ConvertBoolean(value, targetField, errors),
            MappingValueType.Date => ConvertDate(value, format, targetField, errors)?.Date,
            MappingValueType.DateTime => ConvertDate(value, format, targetField, errors),
            MappingValueType.Json => value,
            _ => value
        };
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

    private static object? ConvertDecimal(string? value, string targetField, List<string> errors)
    {
        if (decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        errors.Add($"Field '{targetField}' could not be converted.");
        return null;
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

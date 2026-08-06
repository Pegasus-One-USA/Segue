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
        var referenceLookups = new List<MappingReferenceLookupDto>();

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

            var isJoinedFields = field.Format?.StartsWith("joinedFields", StringComparison.OrdinalIgnoreCase) == true;
            var policy = field.ArrayPolicy;

            var resolved = isJoinedFields
                ? ResolveJoinedFields(root, field)
                : ResolveAll(root, field.JsonPath)
                    .Select(m => (
                        Value: ConvertElement(
                            m.Element, field.ValueType, field.Format, field.TargetField, errors,
                            field.MaxLength, field.Precision, field.Scale),
                        m.Indices))
                    .ToList();

            if (resolved.Count == 0)
            {
                if (policy == ArrayPolicy.SeparateDestination)
                {
                    // No matching element for this occurrence (e.g. an optional sub-field absent on this
                    // particular array item) — this field belongs to a CHILD table, not the parent row, so it
                    // must never fall through to `parent[...]` below. Nothing to contribute for this field on
                    // this occurrence; other fields on the same child table are unaffected.
                    continue;
                }

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

            var values = resolved.Select(r => r.Value).ToList();

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

                    for (var i = 0; i < resolved.Count; i++)
                    {
                        var key = string.Join('-', resolved[i].Indices);
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
                    parent[field.TargetField] = ResolveCorrelatedValue(
                        root, field, resolved.Select(r => r.Indices).ToList(), values, errors);
                    break;

                case ArrayPolicy.Scalar:
                case ArrayPolicy.FirstItem:
                default:
                    parent[field.TargetField] = values[0];
                    break;
            }

            // A field marked as a FHIR reference (e.g. "$.subject.reference" = "Patient/xyz") can't be written
            // verbatim — the target column is normally a FK expecting another table's real primary key, not a
            // bare FHIR id string. Extract that id and record what to resolve it against; the writer performs
            // the actual lookup once the referenced table's own rows have been written. Only meaningful for the
            // single-value policies above (RepeatParent/SeparateDestination/StoreJson fields aren't references).
            if (!string.IsNullOrWhiteSpace(field.ReferenceLookupTable)
                && !string.IsNullOrWhiteSpace(field.ReferenceLookupKeyColumn)
                && policy is ArrayPolicy.Scalar or ArrayPolicy.FirstItem or ArrayPolicy.RejectIfMultiple)
            {
                var rawReference = parent.TryGetValue(field.TargetField, out var rawValue) ? rawValue as string : null;
                referenceLookups.Add(new MappingReferenceLookupDto(
                    field.TargetField, field.ReferenceLookupTable!, field.ReferenceLookupKeyColumn!, ExtractReferenceId(rawReference)));

                // Clear the raw string: an unresolved lookup should fail loudly at write time with a clear
                // "no matching row" error, not a confusing type-conversion error from inserting "Patient/xyz"
                // as-is into what's normally a bigint column.
                parent[field.TargetField] = null;
            }
        }

        var rowsList = BuildParentRows(parent, repeatFields);
        var childTableDtos = childTables
            .Select(kv => new MappingChildTableDto(kv.Key, kv.Value.Values.Cast<IReadOnlyDictionary<string, object?>>().ToList()))
            .ToList();

        return new MappingTestResultDto(
            rowsList[0], errors, rowsList, childTableDtos,
            referenceLookups.Count > 0 ? referenceLookups : null);
    }

    /// <summary>Extracts the resource-local id from a FHIR reference string — "Patient/xyz" or an absolute URL
    /// ending "…/Patient/xyz" both yield "xyz"; a bare id with no "/" is returned as-is. Null/blank input (no
    /// reference present on this resource) yields null — nothing to resolve, so the target column is left
    /// unpopulated rather than guessing.</summary>
    private static string? ExtractReferenceId(string? rawReference)
    {
        if (string.IsNullOrWhiteSpace(rawReference))
        {
            return null;
        }

        var slashIndex = rawReference.LastIndexOf('/');
        return slashIndex >= 0 ? rawReference[(slashIndex + 1)..] : rawReference;
    }

    /// <summary>
    /// Selects the value among <paramref name="indices"/>/<paramref name="values"/> (same order, one per array item)
    /// whose sibling code element — resolved via <see cref="MappingFieldDto.CorrelationCodeJsonPath"/>, sharing the
    /// same outermost array index as <paramref name="indices"/> — equals <see cref="MappingFieldDto.CorrelationCodeValue"/>.
    /// This is how e.g. a blood-pressure Observation's systolic/diastolic <c>component[]</c> entries are told apart:
    /// position alone isn't reliable, but each component carries a LOINC code identifying which reading it is.
    /// </summary>
    private static object? ResolveCorrelatedValue(
        JsonElement root,
        MappingFieldDto field,
        List<IReadOnlyList<int>> indices,
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

        for (var i = 0; i < indices.Count; i++)
        {
            if (indices[i].Count > 0 && matchingOuterIndices.Contains(indices[i][0]))
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
    /// Resolves a "joinedFields" field: <see cref="MappingFieldDto.JsonPath"/> is a <c>|</c>-delimited list of
    /// sub-paths (see <c>MappingImportService.BuildJsonPathAndFormat</c>), each resolved independently and then
    /// joined per row with the delimiter encoded in <see cref="MappingFieldDto.Format"/> (<c>;delimiter=X</c>).
    /// Rows are aligned by position across sub-paths — they're expected to share the same array context, so the
    /// first sub-path that yields any matches determines the row indices; a sub-path with fewer/no matches at a
    /// given position contributes an empty string for that row rather than dropping the row.
    /// </summary>
    private static List<(object? Value, IReadOnlyList<int> Indices)> ResolveJoinedFields(JsonElement root, MappingFieldDto field)
    {
        var delimiter = ParseDelimiter(field.Format);
        var subPaths = field.JsonPath.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var resolvedSubPaths = subPaths.Select(subPath => ResolveAll(root, subPath)).ToList();

        var shape = resolvedSubPaths.OrderByDescending(r => r.Count).FirstOrDefault() ?? [];
        if (shape.Count == 0)
        {
            return [];
        }

        var rows = new List<(object? Value, IReadOnlyList<int> Indices)>(shape.Count);
        for (var i = 0; i < shape.Count; i++)
        {
            var pieces = resolvedSubPaths.Select(matches => i < matches.Count ? ElementToJoinString(matches[i].Element) : string.Empty);
            rows.Add((string.Join(delimiter, pieces), shape[i].Indices));
        }

        return rows;
    }

    private static string ParseDelimiter(string? format)
    {
        if (string.IsNullOrWhiteSpace(format))
        {
            return ",";
        }

        foreach (var part in format.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var equalsIndex = part.IndexOf('=', StringComparison.Ordinal);
            if (equalsIndex > 0 && part[..equalsIndex].Equals("delimiter", StringComparison.OrdinalIgnoreCase))
            {
                return part[(equalsIndex + 1)..];
            }
        }

        return ",";
    }

    private static string ElementToJoinString(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Null or JsonValueKind.Undefined => string.Empty,
            JsonValueKind.String => element.GetString() ?? string.Empty,
            // One of joinedFields' own sub-paths resolved to an array-of-strings element (e.g. a "given" name
            // array) rather than a single scalar — same fix as ConvertElement's String case above, and for the
            // same reason: falling through to element.ToString() would splice the raw JSON array text
            // ('["Camila","Maria"]') into the middle of the otherwise human-readable joined value.
            JsonValueKind.Array => JoinArrayOfStrings(element),
            _ => element.ToString()
        };
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
                element.ValueKind switch
                {
                    JsonValueKind.String => element.GetString(),
                    // A field mapped straight from an array-of-strings element (e.g. Patient.name.given,
                    // Patient.address.line) with no ArrayPolicy fan-out of its own — ResolveAll stops at this
                    // array without descending into it (no trailing "[*]" on this JsonPath segment), so
                    // ConvertElement is asked to produce ONE string for the whole array. Falling through to
                    // element.ToString() below would return the raw JSON array text verbatim (a real,
                    // user-reported bug: the destination column ended up literally storing
                    // '["Camila","Maria"]' as text) instead of a human-readable joined value.
                    JsonValueKind.Array => JoinArrayOfStrings(element),
                    _ => element.ToString()
                },
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

    /// <summary>Joins a JSON array's own primitive items into one human-readable string (e.g.
    /// ["Camila","Maria"] -> "Camila, Maria"). A nested object/array item is skipped rather than dumping its own
    /// raw JSON into the middle of the joined text — this is for a field whose value genuinely is a flat array
    /// of strings/numbers/booleans, not an array of structured objects.</summary>
    private static string JoinArrayOfStrings(JsonElement array)
    {
        var parts = new List<string>();
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind is JsonValueKind.String)
            {
                parts.Add(item.GetString() ?? string.Empty);
            }
            else if (item.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
            {
                parts.Add(item.ToString());
            }
        }

        return string.Join(", ", parts);
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

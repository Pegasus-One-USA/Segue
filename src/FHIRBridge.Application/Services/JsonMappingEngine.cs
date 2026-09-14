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
        // Every field's full resolved value list, BEFORE whatever ArrayPolicy collapses it into `parent` —
        // see MappingTestResultDto.RawArrayValues for why this survives alongside the collapsed view.
        var rawArrayValues = new Dictionary<string, IReadOnlyList<object?>>(StringComparer.OrdinalIgnoreCase);

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
                            field.MaxLength, field.Precision, field.Scale, field.DeferTypeToTransform),
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
                        field.MaxLength, field.Precision, field.Scale, field.DeferTypeToTransform);
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

            // A FHIR reference resolves to "Encounter/abc", but a destination column wants the id alone — the
            // type prefix is redundant (the column already says which resource it points at) and it breaks any
            // join against that resource's own id column, which stores the bare value. Normalized here, at the
            // single point every value passes through, so it applies to every ArrayPolicy rather than only the
            // scalar case. Fields with a ReferenceLookup (the "Resolves to" control) were already getting this
            // via ExtractReferenceId below and are unaffected: that call is idempotent on a bare id. This is
            // what plain reference mappings — the ones WITHOUT "Resolves to" — were missing, which is why
            // Observation.EncounterId stored "Encounter/anon-…" while Encounter.PatientId stored the bare id.
            if (IsFhirReferencePath(field.JsonPath))
            {
                values = values
                    .Select(value => value is string reference ? ExtractReferenceId(reference) : value)
                    .ToList();
            }

            rawArrayValues[field.TargetField] = values;

            // "aggregate=csv" is the payload's own signal for "join every resolved occurrence into one
            // delimited string on the parent row" — no ArrayPolicy value represents that (see
            // MappingImportService.ResolveArrayMetadata's doc comment on why it's encoded onto Format
            // instead), so whatever ArrayPolicy got stored alongside it must never run in its place here.
            // RepeatParent would fan this resource into one row per occurrence that then upsert-collide on
            // the same key, silently keeping only the last; FirstItem would silently drop every occurrence
            // but the first. Checked before the switch so it wins regardless of which policy was stored.
            if (HasCsvAggregate(field.Format))
            {
                parent[field.TargetField] = JoinValues(values);
                continue;
            }

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
                    // A ValueType=Json field's values are ALREADY raw JSON text (ConvertElement returns
                    // element.GetRawText()), so JsonSerializer.Serialize(values) would double-encode them — the
                    // whole node would land in the column as an escaped string ("[{\"..\":..}]") instead of clean
                    // JSON. Emit clean JSON directly: a single node as-is (e.g. "$" → the raw resource object),
                    // multiple occurrences wrapped once into a real JSON array with no re-escaping. Non-JSON value
                    // types (arrays of strings/numbers) still go through Serialize, which is correct for them.
                    parent[field.TargetField] = field.ValueType == MappingValueType.Json
                        ? (values.Count == 1
                            ? values[0]
                            : "[" + string.Join(",", values.Select(v => v?.ToString() ?? "null")) + "]")
                        : JsonSerializer.Serialize(values);
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
            referenceLookups.Count > 0 ? referenceLookups : null,
            rawArrayValues.Count > 0 ? rawArrayValues : null);
    }

    /// <summary>Extracts the resource-local id from a FHIR reference string — "Patient/xyz" or an absolute URL
    /// ending "…/Patient/xyz" both yield "xyz"; a bare id with no "/" is returned as-is. Null/blank input (no
    /// reference present on this resource) yields null — nothing to resolve, so the target column is left
    /// unpopulated rather than guessing.
    ///
    /// A version-specific reference ("Patient/xyz/_history/2") yields "xyz", not "2": the id is the segment
    /// BEFORE "_history", and taking the last segment outright returned the version number — a value that
    /// matches no row and, for a plain (non-FK) mapping, would have been stored as the id itself.</summary>
    private static string? ExtractReferenceId(string? rawReference)
    {
        if (string.IsNullOrWhiteSpace(rawReference))
        {
            return null;
        }

        var reference = rawReference.Trim();

        // "#contained" and "urn:uuid:…" carry no Type/id pair — the whole string IS the identifier.
        if (reference[0] == '#' || reference.StartsWith("urn:", StringComparison.OrdinalIgnoreCase))
        {
            return reference;
        }

        var segments = reference.Split('/');
        var historyIndex = Array.FindIndex(
            segments, segment => segment.Equals("_history", StringComparison.OrdinalIgnoreCase));
        var idIndex = historyIndex > 0 ? historyIndex - 1 : segments.Length - 1;

        return segments[idIndex].Length > 0 ? segments[idIndex] : reference;
    }

    /// <summary>
    /// True when this field reads a FHIR <c>Reference.reference</c> element, whose value is always a
    /// "Type/id" pointer (or an absolute URL ending in one) rather than a plain scalar.
    /// </summary>
    private static bool IsFhirReferencePath(string? jsonPath)
        => jsonPath is not null && jsonPath.TrimEnd().EndsWith(".reference", StringComparison.OrdinalIgnoreCase);

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
            _ => element.ToString()
        };
    }

    /// <summary>True when the field's Format carries the "aggregate=csv" marker BuildJsonPathAndFormat
    /// stamps for the "combine all values into one delimited string" instance selection — regardless of
    /// which mode (directField/joinedFields/wholeNodeAsJson) it's paired with.</summary>
    private static bool HasCsvAggregate(string? format) =>
        format?.Contains("aggregate=csv", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>Joins already-converted field values (one per resolved occurrence) into one delimited
    /// string — the parent-row counterpart to JoinArrayOfStrings, operating on .NET values already produced
    /// by ConvertElement rather than raw JsonElements.</summary>
    private static string JoinValues(IEnumerable<object?> values) =>
        string.Join(", ", values.Select(v => v?.ToString() ?? string.Empty));

    /// <summary>True for a plain single-source field ("directField", or "directField;aggregate=csv") — the
    /// only shape where "the whole array, as one column" is this field's own deliberate choice rather than a
    /// side effect of some other feature (joinedFields, wholeNodeAsJson) that already has its own, different
    /// handling for a repeating element.</summary>
    private static bool IsDirectField(string? format) =>
        format?.StartsWith("directField", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>Joins a JSON array's own scalar items ("given":["Camila","Maria"]) into one delimited string
    /// ("Camila, Maria") instead of letting element.ToString() fall through to the array's raw JSON text
    /// ("[\"Camila\",\"Maria\"]") — the field's own JsonPath resolved to the whole array (no trailing "[*]"
    /// to fan it out into separate rows/columns), so this is the only representation a single String/Json
    /// column can hold. Nested objects/arrays inside the array are skipped rather than stringified, since
    /// there's no sensible flat-text form for those.</summary>
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
        int? scale = null,
        bool deferTypeToTransform = false)
    {
        if (element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        // A transformation rule downstream is what produces this column's real type, so ValueType describes the
        // rule's OUTPUT, not what's in the document here — coercing to it now can only fail (reading a
        // "1980-05-01" birthDate as the Integer a DateMathAge rule will produce), and the failure would be
        // recorded as a mapping error. The mismatch fallback further down already hands the raw value onward in
        // that case; this just skips the pointless attempt and the bogus error it logs. See
        // MappingFieldDto.DeferTypeToTransform.
        if (deferTypeToTransform)
        {
            return element.ValueKind == JsonValueKind.String ? element.GetString() : element.ToString();
        }

        var converted = valueType switch
        {
            MappingValueType.String => ValidateLength(
                element.ValueKind switch
                {
                    JsonValueKind.String => element.GetString(),
                    // Only a plain directField's own array collapses into a delimited string here — a
                    // joinedFields sub-path resolving to an array goes through ElementToJoinString instead
                    // (a distinct, multi-source concern), and wholeNodeAsJson fields never reach this
                    // String branch at all (their ValueType is Json, handled below).
                    JsonValueKind.Array when IsDirectField(format) => JoinArrayOfStrings(element),
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

        // A genuine type mismatch (e.g. a date-shaped source value mapped with ValueType=Integer/Decimal/Boolean
        // because a transformation rule attached to this field — not this raw-copy step — is what's actually
        // supposed to produce the destination's real type) must not silently vanish into null. Every Convert*
        // helper above already recorded why it couldn't convert into `errors`; fall back to the untouched raw
        // value here so a transformation rule downstream still receives something real to work with, instead of
        // null being mistaken for "field genuinely absent" (see TransformNullPolicy.IsNullOrEmpty). Excludes a
        // deliberate constraint rejection (String over max length, Decimal exceeding column precision) — those
        // null the value on purpose because it CAN'T fit the destination, and falling back would defeat that.
        if (converted is null && !IsDeliberateRejection(valueType, precision))
        {
            return element.ValueKind == JsonValueKind.String ? element.GetString() : element.ToString();
        }

        return converted;
    }

    /// <summary>True when a null <see cref="ConvertElement"/>/<see cref="ConvertValue"/> result means "this
    /// value can never fit the destination as configured" rather than "couldn't parse this as the declared
    /// type" — the former (String's ValidateLength, Decimal's precision check) must stay null; the latter
    /// should fall back to the raw value instead (see the caller's own comment).</summary>
    private static bool IsDeliberateRejection(MappingValueType valueType, int? precision) =>
        valueType == MappingValueType.String || (valueType == MappingValueType.Decimal && precision is not null);

    private static object? ConvertValue(
        string value,
        MappingValueType valueType,
        string? format,
        string targetField,
        List<string> errors,
        int? maxLength = null,
        int? precision = null,
        int? scale = null,
        bool deferTypeToTransform = false)
    {
        // See ConvertElement's own comment — a default value feeds the same rule chain the extracted value
        // would have, so it must not be coerced to the rule's output type either.
        if (deferTypeToTransform)
        {
            return value;
        }

        var converted = valueType switch
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

        // Same "don't let a type mismatch silently become null" fallback as ConvertElement — see its comment.
        return converted is null && !IsDeliberateRejection(valueType, precision) ? value : converted;
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

        // `format` doubles as this field's column-mode marker ("directField", "directField;aggregate=csv",
        // "joinedFields;delimiter=...", "wholeNodeAsJson" — see MappingImportService.BuildJsonPathAndFormat)
        // on every field BuildJsonPathAndFormat ever stamps, which is every field imported through the normal
        // pipeline. None of those are valid DateTime.TryParseExact patterns, so treating a mode-marker value
        // as an exact-parse format here always fails, silently nulling every Date/DateTime-typed field
        // (reproduced: Patient.birthDate mapped to NULL with Format="directField"). Only genuinely attempt
        // TryParseExact for a format that isn't one of these known markers — i.e. a real caller-supplied
        // date pattern for a non-ISO source (e.g. HL7 v2's "yyyyMMdd"), which no current column type stamps
        // but the exact-format path exists to support.
        var isModeMarker = format is not null && (
            format.StartsWith("directField", StringComparison.OrdinalIgnoreCase) ||
            format.StartsWith("joinedFields", StringComparison.OrdinalIgnoreCase) ||
            format.StartsWith("wholeNodeAsJson", StringComparison.OrdinalIgnoreCase));

        var isParsed = string.IsNullOrWhiteSpace(format) || isModeMarker
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

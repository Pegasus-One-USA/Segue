using System.Globalization;
using System.Text.Json;
using FHIRBridge.Application.Abstractions.Mapping;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Domain.Enums;

namespace FHIRBridge.Application.Services;

public sealed partial class JsonMappingEngine : IJsonMappingEngine
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
            // "Set default value" column (field-mapping-model.ts's DefaultValueToken '@default'): always
            // writes the literal DefaultValue text, never anything read from the source payload — there IS
            // no source at all for this field, by construction (see MappingRow's own doc comment on why
            // that's what keeps "default" and "mapped from a source" mutually exclusive).
            if (string.Equals(field.JsonPath, "@default", StringComparison.OrdinalIgnoreCase))
            {
                // CreateMappingProfileRequestValidator requires DefaultValue whenever JsonPath is "@default",
                // but that's a save-time guarantee, not a compile-time one — a profile saved before that
                // rule existed could still reach here with none.
                if (string.IsNullOrWhiteSpace(field.DefaultValue))
                {
                    errors.Add($"Field '{field.TargetField}' is set to a literal default value but has none configured.");
                    parent[field.TargetField] = null;
                    continue;
                }

                parent[field.TargetField] = ConvertValue(
                    field.DefaultValue, field.ValueType, field.Format, field.TargetField, errors,
                    field.MaxLength, field.Precision, field.Scale, field.DeferTypeToTransform);
                continue;
            }

            // System-value field: sourced from pipeline/runtime context (run id, write time, resource type, …)
            // rather than the source JSON. The value flows into the row exactly like a normal mapped column.
            if (IsSystemToken(field.JsonPath))
            {
                object? systemValue = null;
                // "@destinationObject" is special-cased to this FIELD's own DestinationObject when it has one
                // — a field on a child/extra table (ArrayPolicy.SeparateDestination, e.g. a Patient.name array
                // fanned out onto dbo.PatientName) must report ITS OWN table, not the profile's primary table
                // the run-level systemValues dictionary carries. Only the primary-table fields (the common
                // case, where DestinationObject is unset) fall back to the profile-level value.
                if (string.Equals(field.JsonPath, "@destinationObject", StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(field.DestinationObject))
                {
                    systemValue = field.DestinationObject;
                }
                else
                {
                    systemValues?.TryGetValue(field.JsonPath, out systemValue);
                }

                if (systemValue is null && field.IsRequired)
                {
                    errors.Add($"Required system field '{field.TargetField}' had no value for token '{field.JsonPath}'.");
                }

                parent[field.TargetField] = systemValue;
                continue;
            }

            var isJoinedFields = field.Format?.StartsWith("joinedFields", StringComparison.OrdinalIgnoreCase) == true;
            var policy = field.ArrayPolicy;

            // Kept alongside the joined strings below so a transform chain can be handed the field's PARTS
            // rather than the one string they were joined into — see the rawArrayValues assignment further
            // down for why a template's {0}/{1} are meaningless without them.
            var joinedRows = isJoinedFields ? ResolveJoinedFieldRows(root, field) : null;

            var resolved = joinedRows is not null
                ? JoinRows(joinedRows, ParseDelimiter(field.Format), field, errors)
                : ResolveAll(root, field.JsonPath)
                    .Select(m => (
                        Value: ConvertElement(
                            m.Element, field.ValueType, field.Format, field.TargetField, errors,
                            field.MaxLength, field.Precision, field.Scale, field.DeferTypeToTransform),
                        m.Indices))
                    .ToList();

            // "index=N" is the payload's own signal for "Nth instance" (see field-mapping-model.ts) — narrows
            // down to just the N-th distinct occurrence of the repeating parent BEFORE the resolved.Count == 0
            // check below, so an out-of-range N (e.g. Instance #5 picked but only 3 telecom entries exist)
            // falls through to the exact same DefaultValue/IsRequired/null handling a genuinely absent optional
            // sub-field already gets, rather than needing its own duplicate branch.
            var instanceIndex = ParseInstanceIndex(field.Format);
            if (instanceIndex is int selectedInstance)
            {
                resolved = SelectInstance(resolved, selectedInstance);
            }

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

            // The transform stage deliberately hands a chain LED by ConcatenationTemplating/ArrayListOperations
            // the full occurrence list rather than the single value an ArrayPolicy collapsed it to, because that
            // is the only way "join every line of the address" can work (see TransformNodeExecutors). But the
            // full list spans EVERY instance of the repeating parent, which silently overrode the field's own
            // Instance Selection: Patient.name.given set to "First" still fed the rule all four values of a
            // payload carrying the same name twice (use=official and use=usual, as Epic sends), and concat wrote
            // "Camila Maria Camila Maria". The screen said First and meant nothing.
            //
            // Narrow the list to the selected instance instead of abandoning the override. Each resolved value
            // carries the index path it came from, so name[0].given[*] is separable from name[1].given[*] while
            // address[0].line[*] — every value under one instance — stays whole and still joins as before.
            // "All records" (RepeatParent, or FirstItem plus the csv aggregate above) remains the way to span
            // every instance, so nothing loses the ability to do so.
            rawArrayValues[field.TargetField] =
                policy is ArrayPolicy.Scalar or ArrayPolicy.FirstItem or ArrayPolicy.RejectIfMultiple
                    ? TakeFirstInstance(resolved, values)
                    : values;

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
    /// same array index CHAIN (every level, not merely the outermost — see <see cref="IsIndexChainMatch"/>) as
    /// <paramref name="indices"/> — satisfies <see cref="MappingFieldDto.CorrelationCodeOperator"/> (default/null:
    /// exact match) against <see cref="MappingFieldDto.CorrelationCodeValue"/>. This is how e.g. a blood-pressure
    /// Observation's systolic/diastolic <c>component[]</c> entries are told apart (one repeating level: position
    /// alone isn't reliable, but each component carries a LOINC code identifying which reading it is), and equally
    /// how a specific <c>Patient.contact[].telecom[].value</c> is picked by that SAME telecom item's own
    /// <c>system</c>/<c>use</c> (two repeating levels: matching on the outer "which contact" index alone would
    /// return that contact's FIRST telecom value, not necessarily the one whose own sibling actually matched).
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
        var matchingIndexChains = codeMatches
            .Where(m => m.Element.ValueKind == JsonValueKind.String &&
                        MatchesCorrelationOperator(field.CorrelationCodeOperator, m.Element.GetString()!, field.CorrelationCodeValue))
            .Select(m => m.Indices)
            .Where(ix => ix.Count > 0)
            .ToList();

        if (matchingIndexChains.Count == 0)
        {
            return null;
        }

        for (var i = 0; i < indices.Count; i++)
        {
            if (matchingIndexChains.Any(chain => IsIndexChainMatch(chain, indices[i])))
            {
                return values[i];
            }
        }

        return null;
    }

    /// <summary>
    /// The comparison a "Match criteria" row's op selects — null/"Equals" (every CorrelateByCode field saved
    /// before this operator existed relied on exact match, so that must stay the default), "Contains" (case-
    /// insensitive substring) or "NotEquals". Unrecognized operator text falls back to "Equals" the same way
    /// a null one does, rather than silently matching nothing.
    /// </summary>
    private static bool MatchesCorrelationOperator(string? operatorName, string siblingValue, string targetValue) =>
        operatorName switch
        {
            "Contains" => siblingValue.Contains(targetValue, StringComparison.OrdinalIgnoreCase),
            "NotEquals" => !string.Equals(siblingValue, targetValue, StringComparison.OrdinalIgnoreCase),
            _ => string.Equals(siblingValue, targetValue, StringComparison.OrdinalIgnoreCase),
        };

    /// <summary>
    /// Whether a correlation match's index chain and a resolved value's own index chain agree on every level
    /// they both have — a single-repeating-level correlation (e.g. Observation.component's LOINC code, chain
    /// length 1) only ever needs to agree on that one outer index, exactly as before this compared full
    /// chains; a two-level correlation (e.g. Patient.contact[].telecom[]'s own sibling, chain length 2) must
    /// agree on BOTH the contact index AND the telecom index — agreeing on the outer index alone would
    /// return the right CONTACT's FIRST telecom value rather than the specific telecom item whose own
    /// sibling actually satisfied the criteria.
    /// </summary>
    private static bool IsIndexChainMatch(IReadOnlyList<int> matchIndices, IReadOnlyList<int> valueIndices)
    {
        var depth = Math.Min(matchIndices.Count, valueIndices.Count);
        if (depth == 0)
        {
            return false;
        }

        for (var i = 0; i < depth; i++)
        {
            if (matchIndices[i] != valueIndices[i])
            {
                return false;
            }
        }

        return true;
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
    /// The values belonging to the FIRST instance of the outermost repeating parent — i.e. everything sharing
    /// the first resolved value's leading index. For "Patient.name.given" over two name entries that is
    /// name[0]'s given names alone; for "Patient.address.line" over one address it is every line, unchanged.
    /// Values with no index path (a non-repeating field) are all kept: there is only one instance.
    /// </summary>
    private static IReadOnlyList<object?> TakeFirstInstance(
        List<(object? Value, IReadOnlyList<int> Indices)> resolved, List<object?> values)
    {
        // `values` is a positional projection of `resolved` (same order, possibly reference-id-normalized), and
        // the loop below indexes one by the other. That holds today; assert it rather than leave it as an
        // invariant a future edit could quietly break into an off-by-one that silently redacts the wrong value.
        if (resolved.Count != values.Count || resolved.Count <= 1 || resolved[0].Indices.Count == 0)
        {
            return values;
        }

        var firstInstance = resolved[0].Indices[0];
        var kept = new List<object?>(resolved.Count);
        for (var i = 0; i < resolved.Count; i++)
        {
            // `values` rather than resolved[i].Value: the reference-id normalization above rewrote them.
            if (resolved[i].Indices.Count > 0 && resolved[i].Indices[0] == firstInstance)
            {
                kept.Add(values[i]);
            }
        }

        return kept.Count > 0 ? kept : values;
    }

    /// <summary>True when the field's Format carries the "index=N" marker the "Nth instance" instance
    /// selection stamps (see field-mapping-model.ts) — the counterpart to <see cref="HasCsvAggregate"/>,
    /// mutually exclusive with it (the UI's instance-selection control offers First/All/Nth/Criteria as one
    /// choice, never two at once). 0-based: 0 means the first occurrence — the same occurrence "First"
    /// (no marker at all) already picks — so a row switched from "First" to "Nth instance" at N=0 behaves
    /// identically.</summary>
    private static int? ParseInstanceIndex(string? format)
    {
        if (string.IsNullOrWhiteSpace(format))
        {
            return null;
        }

        foreach (var part in format.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var equalsIndex = part.IndexOf('=', StringComparison.Ordinal);
            if (equalsIndex > 0
                && part[..equalsIndex].Equals("index", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(part[(equalsIndex + 1)..], out var n))
            {
                return n;
            }
        }

        return null;
    }

    /// <summary>
    /// Narrows `resolved` down to just the values belonging to the (0-based) <paramref name="n"/>-th DISTINCT
    /// instance of the outermost repeating parent — i.e. the n-th entry of the ordered set of
    /// resolved[i].Indices[0] values, NOT literally resolved[n] (a field nested under a second array, e.g.
    /// "name.given" over two names, resolves multiple "given" values per name; "instance 1" must mean
    /// name[1]'s own given list, not the second given value overall — generalizes
    /// <see cref="TakeFirstInstance"/>'s identical framing to any n, not just the first).
    ///
    /// A non-repeating field (resolved[0].Indices is empty — there is only ever one instance) returns
    /// `resolved` unchanged for n == 0 and empty for n &gt; 0: there is no second instance to pick. Empty is
    /// also returned when n has no matching instance at all (out of range) — the caller's existing
    /// "resolved.Count == 0" branch (DefaultValue / IsRequired / null) then applies exactly as it does for a
    /// genuinely absent optional sub-field, rather than this needing its own duplicate fallback.
    /// </summary>
    private static List<(object? Value, IReadOnlyList<int> Indices)> SelectInstance(
        List<(object? Value, IReadOnlyList<int> Indices)> resolved, int n)
    {
        if (resolved.Count == 0)
        {
            return resolved;
        }

        if (resolved[0].Indices.Count == 0)
        {
            return n == 0 ? resolved : [];
        }

        var distinctInstances = new List<int>();
        foreach (var (_, indices) in resolved)
        {
            var outer = indices[0];
            if (!distinctInstances.Contains(outer))
            {
                distinctInstances.Add(outer);
            }
        }

        if (n < 0 || n >= distinctInstances.Count)
        {
            return [];
        }

        var target = distinctInstances[n];
        return resolved.Where(r => r.Indices.Count > 0 && r.Indices[0] == target).ToList();
    }

    /// <summary>
    /// Resolves a "joinedFields" field: <see cref="MappingFieldDto.JsonPath"/> is a <c>|</c>-delimited list of
    /// sub-paths (see <c>MappingImportService.BuildJsonPathAndFormat</c>), each resolved independently and then
    /// joined per row with the delimiter encoded in <see cref="MappingFieldDto.Format"/> (<c>;delimiter=X</c>).
    /// </summary>
    /// <remarks>
    /// Rows are aligned by the OUTERMOST array instance each match came from, not by flat position. Sub-paths
    /// under the same repeating parent fan out at different rates — Patient.name[*].given[*] yields one match
    /// per given name (five, for an Epic payload carrying three name entries) while name[*].family yields one
    /// per name entry (three). Pairing those by index paired "James" with the second name's family and left the
    /// last two rows with no family at all; grouping by name[] instance instead keeps every value with the name
    /// it actually belongs to.
    ///
    /// Within one instance a sub-path can still hold several values (given = ["Warren", "James"]) — those join
    /// with a SPACE, because they are parts of one field, while the configured delimiter separates the distinct
    /// fields being joined. One delimiter box cannot express both levels, and a space is the only sensible
    /// reading of "the given names of this person" (see field-mapping-join-popover).
    ///
    /// A sub-path with no repeating ancestor at all (e.g. Patient.id joined onto a name) contributes its single
    /// value to every row rather than only the first. A sub-path that resolves to nothing for a given instance
    /// contributes no piece at all, so the result is "Warren James" rather than a dangling "Warren James, ".
    /// </remarks>
    private static List<(string[] Pieces, IReadOnlyList<int> Indices)> ResolveJoinedFieldRows(JsonElement root, MappingFieldDto field)
    {
        var subPaths = field.JsonPath.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var resolvedSubPaths = subPaths.Select(subPath => ResolveAll(root, subPath)).ToList();

        // The instance keys to emit a row for: every distinct outermost index any sub-path matched. Taken
        // across ALL sub-paths rather than from whichever matched most, so a sub-path present on an instance
        // the others skipped still gets a row of its own. Sorted below into array order — NOT the order they
        // happened to be discovered in, which depends on which sub-path the user listed first and would make
        // "family|given" emit its rows in a different order than "given|family" for the same document.
        var instanceKeys = new HashSet<int>();
        foreach (var matches in resolvedSubPaths)
        {
            foreach (var match in matches)
            {
                if (match.Indices.Count > 0)
                {
                    instanceKeys.Add(match.Indices[0]);
                }
            }
        }

        // Nothing repeating anywhere — every sub-path is a plain scalar, so there is exactly one row.
        if (instanceKeys.Count == 0)
        {
            if (resolvedSubPaths.All(matches => matches.Count == 0))
            {
                return [];
            }

            var scalarPieces = resolvedSubPaths
                .Select(matches => JoinInstancePieces(matches.Select(m => m.Element)))
                .Where(piece => piece.Length > 0)
                .ToArray();
            return [(scalarPieces, Array.Empty<int>())];
        }

        var orderedKeys = instanceKeys.Order().ToList();

        var rows = new List<(string[] Pieces, IReadOnlyList<int> Indices)>(orderedKeys.Count);
        foreach (var instanceKey in orderedKeys)
        {
            var pieces = resolvedSubPaths
                .Select(matches => JoinInstancePieces(matches
                    // A sub-path with no repeating ancestor carries no indices at all — it belongs to every
                    // instance equally, so it is never filtered out by the instance key.
                    .Where(m => m.Indices.Count == 0 || m.Indices[0] == instanceKey)
                    .Select(m => m.Element)))
                .Where(piece => piece.Length > 0)
                .ToArray();

            rows.Add((pieces, new[] { instanceKey }));
        }

        return rows;
    }

    /// <summary>Collapses each joined row's pieces into the single delimited value the column stores.</summary>
    /// <remarks>
    /// Routed through <see cref="ConvertValue"/> for the same reason every other resolved value is: the
    /// declared ValueType/MaxLength/Precision/Scale describe the DESTINATION COLUMN, and a joined value has
    /// to satisfy them exactly like a single-source one. Returning the raw string here instead meant none of
    /// them ever applied to a multi-source column — a join into a typed date/integer column handed Postgres a
    /// delimited string (42804 at write time rather than a mapping error naming the field), and a join
    /// overflowing a varchar(n) failed the whole write where the same value from ONE source would have been
    /// rejected up front by ValidateLength. ConvertValue's own mismatch fallback still hands the untouched
    /// string onward, so a field whose real type comes from a downstream transform is unaffected.
    /// </remarks>
    private static List<(object? Value, IReadOnlyList<int> Indices)> JoinRows(
        List<(string[] Pieces, IReadOnlyList<int> Indices)> rows,
        string delimiter,
        MappingFieldDto field,
        List<string> errors) =>
        rows.Select(row => (
            ConvertValue(
                string.Join(delimiter, row.Pieces), field.ValueType, field.Format, field.TargetField, errors,
                field.MaxLength, field.Precision, field.Scale, field.DeferTypeToTransform),
            row.Indices)).ToList();

    /// <summary>Joins the values one sub-path contributed for a single array instance. Space-separated: these
    /// are repeats of ONE field (the two given names of one person), not the distinct fields the configured
    /// delimiter separates. Empty/absent values are dropped rather than padding the string with separators.</summary>
    private static string JoinInstancePieces(IEnumerable<JsonElement> elements) =>
        string.Join(' ', elements.Select(ElementToJoinString).Where(text => !string.IsNullOrEmpty(text)));

    private static string ParseDelimiter(string? format)
    {
        if (string.IsNullOrWhiteSpace(format))
        {
            return ",";
        }

        // NOT TrimEntries: ", " is the single most common delimiter a user types, and trimming the part would
        // silently hand back "," instead — the space is part of the value, not formatting around it. Only the
        // KEY is trimmed, so "delimiter=, " still matches while keeping its space.
        foreach (var part in format.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var equalsIndex = part.IndexOf('=', StringComparison.Ordinal);
            if (equalsIndex > 0 && part[..equalsIndex].Trim().Equals("delimiter", StringComparison.OrdinalIgnoreCase))
            {
                return part[(equalsIndex + 1)..];
            }
        }

        return ",";
    }

    /// <summary>Renders one resolved element as text for a join.</summary>
    /// <remarks>
    /// An ARRAY of scalars collapses to its space-joined items rather than its raw JSON text. Whether a
    /// source path carries the inner wildcard is not something the user controls or even sees: the field
    /// catalog supplies "$.name[*].given[*]" for a source it knows about, while a source it has no entry for
    /// is derived as "$.name[*].given" — the first fans out into two matches, the second resolves to the
    /// whole ["Warren","James"] array. Without this, the same join would write "Warren James" or the literal
    /// text [&quot;Warren&quot;,&quot;James&quot;] depending on which of the two it happened to get.
    /// Non-scalar items (objects, nested arrays) have no sensible flat form and are skipped, matching
    /// JoinArrayOfStrings.
    /// </remarks>
    private static string ElementToJoinString(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Null or JsonValueKind.Undefined => string.Empty,
            JsonValueKind.String => element.GetString() ?? string.Empty,
            JsonValueKind.Array => string.Join(' ', element.EnumerateArray()
                .Where(item => item.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array
                    or JsonValueKind.Null or JsonValueKind.Undefined))
                .Select(item => item.ValueKind == JsonValueKind.String ? item.GetString() ?? string.Empty : item.ToString())
                .Where(text => text.Length > 0)),
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
    /// <remarks>
    /// Deliberately ", " and NOT the field's own configured delimiter. A joined field has TWO levels of
    /// separation and the delimiter box only configures the inner one:
    ///
    ///   "Warren James McGinnis, Warren James McGinnis, Warren McGinnis"
    ///    ^^^^^^^^^^^^^^^^^^^^^ one name[] instance, its given+family joined by the configured delimiter
    ///                         ^^ instances joined by THIS, always ", "
    ///
    /// Using the configured delimiter here too collapses the two levels together, so a "full name" column set
    /// to a space delimiter runs every name entry into one unreadable line. See ResolveJoinedFields, which
    /// owns the inner join.
    /// </remarks>
    private static string JoinValues(IEnumerable<object?> values) =>
        string.Join(", ", values.Select(v => v?.ToString() ?? string.Empty));

    /// <summary>Joins a JSON array's own scalar items ("given":["Camila","Maria"]) into one delimited string
    /// ("Camila, Maria") instead of letting element.ToString() fall through to the array's raw JSON text
    /// ("[\"Camila\",\"Maria\"]") — the field's own JsonPath resolved to the whole array (no trailing "[*]"
    /// to fan it out into separate rows/columns), so this is the only representation a single String/Json
    /// column can hold. Nested objects/arrays inside the array are skipped rather than stringified, since
    /// there's no sensible flat-text form for those.</summary>
    /// <summary>
    /// True when every item is a scalar, so <see cref="JoinArrayOfStrings"/> can represent the whole array.
    ///
    /// That join keeps only strings/numbers/booleans and DROPS objects and nested arrays, which is correct for
    /// "given":["Camila","Maria"] and catastrophic for "telecom":[{...},{...}] — every item is dropped and the
    /// column receives an empty string while the run reports success. An array holding anything non-scalar has
    /// no faithful single-column text form, so it keeps its raw JSON rather than being silently emptied.
    /// </summary>
    private static bool IsScalarArray(JsonElement array)
    {
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
            {
                return false;
            }
        }

        return true;
    }

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
            return element.ValueKind switch
            {
                JsonValueKind.String => element.GetString(),
                // An ARRAY must not be handed on as its raw JSON text. element.ToString() yields
                // ["a","b"] — brackets, quotes and all — and that string is what lands in the destination
                // column when no rule reshapes it, which is never what a text column wants. It also defeats
                // the rule that was the whole reason for deferring: the array collapses to ONE value, so
                // ConcatenationTemplating/ArrayListOperations see a single string instead of the items and
                // pass it through unchanged. Join the elements, exactly as the String branch below does.
                // Only a scalar array can be joined — see IsScalarArray. Anything else keeps its raw JSON,
                // which is lossy for a text column but not DESTRUCTIVE, and is what this path produced before.
                JsonValueKind.Array => IsScalarArray(element) ? JoinArrayOfStrings(element) : element.ToString(),
                _ => element.ToString(),
            };
        }

        var converted = valueType switch
        {
            MappingValueType.String => ValidateLength(
                element.ValueKind switch
                {
                    JsonValueKind.String => element.GetString(),
                    // An array collapses into a delimited string — never its raw JSON text. A joinedFields
                    // sub-path resolving to an array goes through ElementToJoinString instead (a distinct,
                    // multi-source concern) and does not reach here, and wholeNodeAsJson fields never reach
                    // this String branch at all (their ValueType is Json, handled below).
                    //
                    // This used to apply only to a plain directField, so any other format wrote ["a","b"]
                    // — brackets and quotes — into a text column. Whatever the format, a String column wants
                    // the values, not a JSON document; a field that genuinely wants JSON declares ValueType
                    // Json and is handled below.
                    JsonValueKind.Array when IsScalarArray(element) => JoinArrayOfStrings(element),
                    _ => element.ToString()
                },
                maxLength, targetField, errors),
            MappingValueType.Integer => ConvertInteger(element.ToString(), targetField, errors),
            MappingValueType.Decimal => ConvertDecimal(element.ToString(), targetField, errors, precision, scale),
            MappingValueType.Boolean => ConvertBoolean(element.ToString(), targetField, errors),
            MappingValueType.Date => ConvertDateOnly(element.ToString(), format, targetField, errors),
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
            MappingValueType.Date => ConvertDateOnly(value, format, targetField, errors),
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

        // Only ever called for a DateTime/instant-typed field now (a Date-typed one uses ConvertDateOnly
        // below instead — a plain calendar date has no time zone to normalize through UTC at all).
        // AdjustToUniversal|AssumeUniversal (not AssumeUniversal alone) resolves the true UTC instant an
        // offset-bearing source value (e.g. a FHIR instant/dateTime) actually represents — AssumeUniversal
        // alone instead converts it to THIS PROCESS's own local system time zone, so the exact same input
        // parses to a different value depending on which machine happens to run it (verified empirically:
        // "2026-03-14T22:00:00Z" -> 2026-03-15T03:30 local on a UTC+05:30 host). Kind is then reset to
        // Unspecified so this native pass-through DateTime binds identically regardless of destination —
        // Npgsql only treats a bare Kind=Utc DateTime as "timestamptz" (see
        // MappingNodeExecutor.CoerceToExpectedValueType's identical fix, which this mirrors).
        var isParsed = string.IsNullOrWhiteSpace(format) || isModeMarker
            ? DateTime.TryParse(
                value, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsedDate)
            : DateTime.TryParseExact(
                value, format, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out parsedDate);

        if (isParsed)
        {
            return DateTime.SpecifyKind(parsedDate, DateTimeKind.Unspecified);
        }

        errors.Add($"Field '{targetField}' could not be converted to a date/time.");
        return null;
    }

    /// <summary>
    /// A FHIR `date` has no time zone at all — unlike `dateTime`/`instant`, there is no "true UTC instant" to
    /// normalize through, and DateTimeFormatNode's own "date" output (a DateTimeOffset formatted straight to
    /// "yyyy-MM-dd", never adjusted to UTC — see its own TryParse/Execute) already reflects that.
    /// DateTimeOffset.TryParse (unlike DateTime.TryParse, which ConvertDate above uses) never converts to
    /// this process's own system time zone even without AdjustToUniversal, so taking its own .Date is
    /// host-independent without doing any UTC conversion at all — unlike ConvertDate's old shared behavior,
    /// which (before this method existed) normalized through UTC first and so changed the CALENDAR DAY itself
    /// for an offset-bearing input (e.g. "2026-03-14T20:00:00-05:00" became 2026-03-15).
    ///
    /// A bare year ("2020") or year-month ("2020-05") is valid FHIR date precision on its own —
    /// DateTimeFormatNode deliberately emits it unchanged rather than fabricate a day (see its own identical
    /// regex guard). Coercing it into a full date here would silently invent a day for real patient data, so
    /// this records why and returns null instead — the caller's existing "don't let a type mismatch silently
    /// become null" fallback (see ConvertElement/ConvertValue) then passes the raw partial-precision string
    /// through unconverted, same as it already does for any other unparseable Date input, rather than writing
    /// a fabricated day that reads as if it were real.
    /// </summary>
    private static DateTime? ConvertDateOnly(
        string? value,
        string? format,
        string targetField,
        List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (PartialDatePattern().IsMatch(value))
        {
            errors.Add($"Field '{targetField}' has only year/year-month precision.");
            return null;
        }

        var isModeMarker = format is not null && (
            format.StartsWith("directField", StringComparison.OrdinalIgnoreCase) ||
            format.StartsWith("joinedFields", StringComparison.OrdinalIgnoreCase) ||
            format.StartsWith("wholeNodeAsJson", StringComparison.OrdinalIgnoreCase));

        var isParsed = string.IsNullOrWhiteSpace(format) || isModeMarker
            ? DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsedDate)
            : DateTimeOffset.TryParseExact(value, format, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out parsedDate);

        if (isParsed)
        {
            return DateTime.SpecifyKind(parsedDate.Date, DateTimeKind.Unspecified);
        }

        errors.Add($"Field '{targetField}' could not be converted to a date.");
        return null;
    }

    // Mirrors MappingNodeExecutor.CoerceDate's (and DateTimeFormatNode's) identical partial-FHIR-date guard.
    [System.Text.RegularExpressions.GeneratedRegex(@"^\d{4}(-\d{2})?$")]
    private static partial System.Text.RegularExpressions.Regex PartialDatePattern();
}

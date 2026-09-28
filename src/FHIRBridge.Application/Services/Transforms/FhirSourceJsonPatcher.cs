using System.Text.Json;
using System.Text.Json.Nodes;

namespace FHIRBridge.Application.Services.Transforms;

/// <summary>
/// Writes a transform-rule's output value back into a source FHIR resource's own JSON, at a rule-configured
/// path (<see cref="FHIRBridge.Domain.Entities.TransformationRule.FhirWriteBackJsonPath"/>) — the mechanism that lets a
/// FHIR-native destination (Aidbox, Medplum, any other FhirRepository-typed config) receive an enriched value
/// (e.g. a CodeableConceptBuilder result with a real terminology display) instead of the untouched original
/// coding a flat/tabular destination would otherwise be the only place to see it.
/// </summary>
/// <remarks>
/// Path syntax: dot-separated segments, each optionally suffixed with "[n]" (a specific array index) or
/// "[]"/"[*]" (treated as index 0) — no leading "$." (unlike the JsonPath convention used for reading). Missing
/// intermediate objects/arrays are created as needed; an array shorter than a requested index is padded with
/// empty objects. Known v1 limitation: an index beyond a small, reasonable bound is still honored (no upper
/// cap), and there is no fan-out across an existing multi-element array — every write targets exactly one
/// element, matching the common "there's one coding/one component" case this exists for.
/// </remarks>
public static class FhirSourceJsonPatcher
{
    /// <summary>Parses <paramref name="sourceJson"/>, applies every (path, value) pair, and returns the patched
    /// JSON text — or the original <paramref name="sourceJson"/> unchanged if it isn't a JSON object, or
    /// <paramref name="patches"/> is empty.</summary>
    public static string? ApplyPatches(string? sourceJson, IReadOnlyList<(string Path, object? Value)> patches)
    {
        if (string.IsNullOrWhiteSpace(sourceJson) || patches.Count == 0)
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
            // Not parseable JSON — leave it exactly as-is rather than lose the original record.
            return sourceJson;
        }

        if (root is not JsonObject rootObject)
        {
            return sourceJson;
        }

        foreach (var (path, value) in patches)
        {
            SetAtPath(rootObject, path, value);
        }

        // Relaxed encoder: the default escapes < > & into \uXXXX form, which would rewrite a patched-in value
        // like "<=200" into "<=200" in the resource a FHIR-native destination receives. Legal JSON either
        // way, but only one of them survives a human reading the record.
        return rootObject.ToJsonString(RelaxedJsonOptions);
    }

    private static readonly JsonSerializerOptions RelaxedJsonOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>Reads the sibling "display" value next to a "...code" leaf in the source resource's own JSON —
    /// e.g. given source field path "code.coding.code", reads "code.coding.display" (taking the first element
    /// of any array segment along the way, matching the mapping engine's own "First" instance-selection
    /// convention). Feeds <see cref="ReservedTransformConfigKeys.SourceDisplayHint"/>.
    /// Returns null when <paramref name="sourceFieldPath"/> doesn't end in a "code" segment, the JSON isn't
    /// parseable, or no string value exists at the derived path — any of which just means this fallback tier
    /// has nothing to offer, not an error.</summary>
    public static string? TryReadSiblingDisplay(string? sourceJson, string? sourceFieldPath)
    {
        var segments = SplitLeafPath(sourceFieldPath, "code");
        return segments is null ? null : ReadSiblingString(sourceJson, segments, "display");
    }

    /// <summary>Reads the unit the source resource already carried next to a Quantity's own "...value" leaf —
    /// e.g. given source field path "$.valueQuantity.value", reads "valueQuantity.unit", falling back to the
    /// UCUM "valueQuantity.code" when the element carries only the coded form. Feeds
    /// <see cref="ReservedTransformConfigKeys.SourceUnitHint"/>, which
    /// <see cref="Nodes.QuantityRangeAssemblyNode"/> uses when the rule itself configures no unit — without it
    /// an assembled Quantity emits <c>"unit": ""</c> even though the source said "mg/dL".
    /// Returns null when the path doesn't end in a "value" segment, the JSON isn't parseable, or neither
    /// sibling holds a string — all of which just mean there's nothing to carry over, not an error.</summary>
    public static string? TryReadSiblingQuantityUnit(string? sourceJson, string? sourceFieldPath)
    {
        var segments = SplitLeafPath(sourceFieldPath, "value");
        if (segments is null)
        {
            return null;
        }

        // Parsed ONCE and reused for both sibling names. This runs per field, per rule, per resource in the
        // Runtime pipeline's mapping loop, so parsing the whole resource twice per call turned a batch of a
        // few thousand Observations into twice that many full-document parses for no benefit.
        var root = TryParseSource(sourceJson);

        return ReadSiblingFrom(root, segments, "unit") ?? ReadSiblingFrom(root, segments, "code");
    }

    /// <summary>Splits a rule's source field path into segments, but only when its leaf segment is
    /// <paramref name="expectedLeaf"/> — the guard that keeps a sibling hint from being read off an unrelated
    /// path. Strips the JsonPath "$." prefix the mapping profile stores (source fields arrive as "$.a.b", not
    /// "a.b"), so the traversal below starts at a real property name rather than at "$".</summary>
    private static string[]? SplitLeafPath(string? sourceFieldPath, string expectedLeaf)
    {
        if (string.IsNullOrWhiteSpace(sourceFieldPath))
        {
            return null;
        }

        var bare = sourceFieldPath.StartsWith("$.", StringComparison.Ordinal)
            ? sourceFieldPath[2..]
            : sourceFieldPath.TrimStart('$', '.');

        var segments = bare.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length == 0)
        {
            return null;
        }

        var (leafName, _) = ParseSegment(segments[^1]);
        return string.Equals(leafName, expectedLeaf, StringComparison.OrdinalIgnoreCase) ? segments : null;
    }

    /// <summary>Walks <paramref name="segments"/> through the source JSON with the leaf replaced by
    /// <paramref name="siblingName"/>, honoring "[n]"/"[]"/"[*]" indices via <see cref="ParseSegment"/> and
    /// otherwise taking the first element of any array encountered (the mapping engine's own "First"
    /// instance-selection convention).</summary>
    private static string? ReadSiblingString(string? sourceJson, string[] segments, string siblingName) =>
        ReadSiblingFrom(TryParseSource(sourceJson), segments, siblingName);

    /// <summary>Parses the source document for a sibling lookup, yielding null for the two cases every
    /// TryReadSibling* method treats identically: there is nothing to parse, or it isn't valid JSON.</summary>
    /// <remarks>
    /// Shared rather than inlined per caller because the null guard and the JsonException catch are NOT
    /// interchangeable and an inlined copy once kept only the second. <c>JsonNode.Parse(null)</c> throws
    /// <see cref="ArgumentNullException"/>, which <c>catch (JsonException)</c> does not cover — so a null
    /// document escaped as an unhandled exception and took down the whole transform for that resource,
    /// instead of the documented "nothing to offer, not an error" of a missing hint. Every one of these
    /// methods takes a <c>string?</c> and promises null on unparseable input, so the guard belongs in the
    /// one place they all go through.
    /// </remarks>
    private static JsonNode? TryParseSource(string? sourceJson)
    {
        if (string.IsNullOrWhiteSpace(sourceJson))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(sourceJson);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The traversal half of <see cref="ReadSiblingString"/>, over an ALREADY-parsed document — so a
    /// caller reading two sibling names off the same resource pays one parse, not two.</summary>
    private static string? ReadSiblingFrom(JsonNode? root, string[] segments, string siblingName)
    {
        var current = root;

        for (var i = 0; i < segments.Length; i++)
        {
            var (propertyName, arrayIndex) = ParseSegment(segments[i]);
            if (i == segments.Length - 1)
            {
                // The leaf's own "[n]" goes with the leaf, not with the sibling replacing it. A rule on
                // "...valueQuantity.value[0]" says WHICH value it means; the sibling "unit" is a different
                // property that has its own shape, and indexing into it because the leaf was indexed reads a
                // subscript the path never asked for — returning null, or worse the wrong element, for a
                // sibling that is itself an array.
                propertyName = siblingName;
                arrayIndex = null;
            }

            if (current is JsonArray outerArray)
            {
                current = outerArray.Count > 0 ? outerArray[0] : null;
            }

            if (current is not JsonObject obj || !obj.TryGetPropertyValue(propertyName, out current))
            {
                return null;
            }

            if (arrayIndex is not null && current is JsonArray indexed)
            {
                current = indexed.Count > arrayIndex.Value ? indexed[arrayIndex.Value] : null;
            }
        }

        if (current is JsonArray finalArray)
        {
            current = finalArray.Count > 0 ? finalArray[0] : null;
        }

        return current is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
    }

    private static void SetAtPath(JsonObject root, string path, object? value)
    {
        var segments = path.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length == 0)
        {
            return;
        }

        JsonObject current = root;
        for (var i = 0; i < segments.Length; i++)
        {
            var (propertyName, arrayIndex) = ParseSegment(segments[i]);
            if (string.IsNullOrEmpty(propertyName))
            {
                return;
            }

            var isLast = i == segments.Length - 1;

            if (arrayIndex is null)
            {
                // A segment with no explicit index that lands on an existing ARRAY must write into that
                // array's first element — never over the array itself. FhirJsonPathReader reads exactly that
                // element, so anything else means the rule reads one node and writes another; and because
                // FHIR cardinality is structural, replacing a repeating element with a bare object/value
                // produces a resource the server rejects outright. This is the "expected-array" 422 a rule on
                // Patient.name.family used to cause: "name" resolved to an array, failed the object test
                // below, and the whole array was replaced by { "family": ... }.
                if (current[propertyName] is JsonArray existingArray)
                {
                    if (existingArray.Count == 0)
                    {
                        existingArray.Add(isLast ? ToJsonNode(value) : new JsonObject());
                        if (isLast)
                        {
                            return;
                        }
                    }
                    else if (isLast)
                    {
                        existingArray[0] = ToJsonNode(value);
                        return;
                    }

                    if (existingArray[0] is not JsonObject arrayElement)
                    {
                        arrayElement = new JsonObject();
                        existingArray[0] = arrayElement;
                    }

                    current = arrayElement;
                    continue;
                }

                if (isLast)
                {
                    current[propertyName] = ToJsonNode(value);
                    return;
                }

                if (current[propertyName] is not JsonObject child)
                {
                    child = new JsonObject();
                    current[propertyName] = child;
                }

                current = child;
            }
            else
            {
                if (current[propertyName] is not JsonArray array)
                {
                    array = new JsonArray();
                    current[propertyName] = array;
                }

                while (array.Count <= arrayIndex.Value)
                {
                    array.Add(new JsonObject());
                }

                if (isLast)
                {
                    array[arrayIndex.Value] = ToJsonNode(value);
                    return;
                }

                if (array[arrayIndex.Value] is not JsonObject arrayChild)
                {
                    arrayChild = new JsonObject();
                    array[arrayIndex.Value] = arrayChild;
                }

                current = arrayChild;
            }
        }
    }

    /// <summary>Splits one path segment into its property name and optional array index. Internal rather than
    /// private so <see cref="FhirJsonPathReader"/> parses paths with the exact same rules this writes them by —
    /// a reader that disagreed with the writer about "[]" or a missing index would read one element and write
    /// another.</summary>
    internal static (string PropertyName, int? ArrayIndex) ParseSegment(string segment)
    {
        var bracketIndex = segment.IndexOf('[', StringComparison.Ordinal);
        if (bracketIndex < 0 || !segment.EndsWith("]", StringComparison.Ordinal))
        {
            return (segment, null);
        }

        var propertyName = segment[..bracketIndex];
        var indexText = segment[(bracketIndex + 1)..^1];
        var arrayIndex = indexText is "" or "*" ? 0 : int.TryParse(indexText, out var parsed) ? parsed : 0;
        return (propertyName, arrayIndex);
    }

    private static JsonNode? ToJsonNode(object? value)
    {
        if (value is null)
        {
            return null;
        }

        if (value is JsonNode node)
        {
            return node.DeepClone();
        }

        return JsonSerializer.SerializeToNode(value);
    }
}

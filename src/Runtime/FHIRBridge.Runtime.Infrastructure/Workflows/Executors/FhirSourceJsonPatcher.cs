using System.Text.Json;
using System.Text.Json.Nodes;

namespace FHIRBridge.Runtime.Infrastructure.Workflows.Executors;

/// <summary>
/// Writes a transform-rule's output value back into a source FHIR resource's own JSON, at a rule-configured
/// path (<see cref="Domain.Entities.TransformationRule.FhirWriteBackJsonPath"/>) — the mechanism that lets a
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

        return rootObject.ToJsonString();
    }

    /// <summary>Reads the sibling "display" value next to a "...code" leaf in the source resource's own JSON —
    /// e.g. given source field path "code.coding.code", reads "code.coding.display" (taking the first element
    /// of any array segment along the way, matching the mapping engine's own "First" instance-selection
    /// convention). Feeds <see cref="Application.Services.Transforms.ReservedTransformConfigKeys.SourceDisplayHint"/>.
    /// Returns null when <paramref name="sourceFieldPath"/> doesn't end in a "code" segment, the JSON isn't
    /// parseable, or no string value exists at the derived path — any of which just means this fallback tier
    /// has nothing to offer, not an error.</summary>
    public static string? TryReadSiblingDisplay(string? sourceJson, string? sourceFieldPath)
    {
        if (string.IsNullOrWhiteSpace(sourceJson) || string.IsNullOrWhiteSpace(sourceFieldPath))
        {
            return null;
        }

        var segments = sourceFieldPath.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length == 0 || !string.Equals(segments[^1], "code", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        segments[^1] = "display";

        JsonNode? current;
        try
        {
            current = JsonNode.Parse(sourceJson);
        }
        catch (JsonException)
        {
            return null;
        }

        foreach (var segment in segments)
        {
            if (current is JsonArray array)
            {
                current = array.Count > 0 ? array[0] : null;
            }

            if (current is not JsonObject obj || !obj.TryGetPropertyValue(segment, out current))
            {
                return null;
            }
        }

        if (current is JsonArray finalArray)
        {
            current = finalArray.Count > 0 ? finalArray[0] : null;
        }

        return current is JsonValue value && value.TryGetValue<string>(out var display) ? display : null;
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

    private static (string PropertyName, int? ArrayIndex) ParseSegment(string segment)
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

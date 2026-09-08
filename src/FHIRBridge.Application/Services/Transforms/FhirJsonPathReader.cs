using System.Text.Json;
using System.Text.Json.Nodes;

namespace FHIRBridge.Application.Services.Transforms;

/// <summary>
/// Reads a value out of a FHIR resource's own JSON by the same path syntax <see cref="FhirSourceJsonPatcher"/>
/// writes with — the read half of the FHIR-resource transform loop, which the patcher (write-only) never had.
/// </summary>
/// <remarks>
/// Path syntax: dot-separated segments, each optionally suffixed with "[n]" (a specific array index) or
/// "[]"/"[*]" (index 0) — no leading "$.". A segment with no index that lands on an array takes its FIRST
/// element, matching both the patcher's write behavior and the mapping engine's own "First" instance-selection
/// convention, so a rule authored against "name.given" reads the value the same rule would write back.
///
/// Returns null whenever the path doesn't resolve — a missing element is not an error, it is the input the
/// rule's own <see cref="Domain.Enums.NullPolicy"/> exists to decide about (see <see cref="TransformNullPolicy"/>).
/// </remarks>
public static class FhirJsonPathReader
{
    /// <summary>Parses <paramref name="sourceJson"/> and reads the value at <paramref name="path"/>, unwrapping
    /// JSON primitives to CLR values (string/bool/long/double) so a transform node receives the same shape it
    /// would from the mapping engine. Objects and arrays come back as <see cref="JsonNode"/>, which is what the
    /// FHIR complex-type nodes already accept and emit.</summary>
    public static object? Read(string? sourceJson, string? path)
    {
        if (string.IsNullOrWhiteSpace(sourceJson) || string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(sourceJson);
        }
        catch (JsonException)
        {
            // Unparseable JSON is the record's problem, not this rule's — the caller leaves the resource
            // untouched rather than failing the batch.
            return null;
        }

        return Unwrap(ReadNode(root, path));
    }

    /// <summary>Node-level read, for a caller that already parsed the document once and is walking several
    /// paths across the same resource — the per-resource case this exists for.</summary>
    public static JsonNode? ReadNode(JsonNode? root, string? path)
    {
        if (root is null || string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var current = root;
        foreach (var segment in path.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var (propertyName, arrayIndex) = FhirSourceJsonPatcher.ParseSegment(segment);
            if (string.IsNullOrEmpty(propertyName))
            {
                return null;
            }

            // An intermediate array with no index of its own: take the first element, then keep descending.
            if (current is JsonArray implicitArray)
            {
                current = implicitArray.Count > 0 ? implicitArray[0] : null;
            }

            if (current is not JsonObject obj || !obj.TryGetPropertyValue(propertyName, out var next))
            {
                return null;
            }

            current = next;

            if (arrayIndex is not null)
            {
                if (current is not JsonArray array || arrayIndex.Value >= array.Count)
                {
                    return null;
                }

                current = array[arrayIndex.Value];
            }
        }

        // A path whose final segment carried no index but resolved to an array still yields one value, for the
        // same reason the intermediate case does — "name.given" means the given name, not the list.
        if (current is JsonArray finalArray)
        {
            current = finalArray.Count > 0 ? finalArray[0] : null;
        }

        return current;
    }

    /// <summary>Turns a JSON primitive into the CLR value a transform node expects; objects and arrays pass
    /// through as <see cref="JsonNode"/>.</summary>
    public static object? Unwrap(JsonNode? node)
    {
        if (node is not JsonValue value)
        {
            return node;
        }

        if (value.TryGetValue<string>(out var stringValue))
        {
            return stringValue;
        }

        if (value.TryGetValue<bool>(out var boolValue))
        {
            return boolValue;
        }

        // long before double so an integral value doesn't acquire a spurious ".0" when a node stringifies it.
        if (value.TryGetValue<long>(out var longValue))
        {
            return longValue;
        }

        return value.TryGetValue<double>(out var doubleValue) ? doubleValue : value.ToJsonString();
    }
}

using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace FHIRBridge.Runtime.Application.Transformations;

/// <summary>
/// Pre-write FHIR conformance sanitizer. Real EHR sources (eCW, Epic, Cerner, athenahealth, …) routinely emit
/// resources that violate FHIR invariants or carry malformed free text; strict destinations (Medplum, Aidbox, a
/// validation-enabled HAPI server) then reject those records with HTTP 400 while accepting everything else. This
/// makes a resource conformant BEFORE it is written so a handful of dirty source records don't fail against strict
/// servers.
///
/// It only ever removes or cleans offending content — a resource that is already conformant passes through
/// byte-for-byte unchanged (the caller can compare references to detect a no-op). It is failure-isolated: JSON that
/// doesn't parse, or isn't a FHIR resource object, is returned untouched. Applied at the pre-write choke points on
/// both runtime planes (see FhirResourceNormalizer and DestinationNodeExecutor), so every destination benefits;
/// lenient destinations are unaffected because conformant data is left as-is.
/// </summary>
public static class FhirConformanceSanitizer
{

    private static readonly JsonSerializerOptions SerializeOptions = new()
    {
        // Match the repo's other JsonNode mutators (FhirFieldTransformApplier): don't over-escape non-ASCII text.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// Returns a conformance-sanitized copy of <paramref name="json"/>, or the original string unchanged when
    /// nothing needed fixing (or it couldn't be parsed as a FHIR resource). <paramref name="resourceType"/> may be
    /// null — it's then read from the resource's own <c>resourceType</c> element.
    /// </summary>
    public static string? Sanitize(string? resourceType, string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return json;
        }

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return json;
        }

        if (root is not JsonObject resource)
        {
            return json;
        }

        var type = string.IsNullOrEmpty(resourceType)
            ? resource["resourceType"]?.GetValue<string>()
            : resourceType;

        var changed = false;

        // Rule 1 — FHIR invariant con-5: Condition.clinicalStatus SHALL NOT be present when verificationStatus is
        // entered-in-error. eCW carries clinicalStatus regardless of verification status, so strip it in that case.
        if (string.Equals(type, "Condition", StringComparison.OrdinalIgnoreCase))
        {
            changed |= RemoveClinicalStatusIfEnteredInError(resource);
        }

        // Rule 2 — a FHIR `string` primitive may not contain C0 control characters (other than tab/CR/LF) and may not
        // be whitespace-only or whitespace-padded; malformed free text (e.g. eCW's Encounter.reasonCode.text) is
        // rejected as "invalid string format". Recursively clean every JSON string value in the resource.
        changed |= SanitizeStrings(root);

        return changed ? resource.ToJsonString(SerializeOptions) : json;
    }

    private static bool RemoveClinicalStatusIfEnteredInError(JsonObject resource)
    {
        if (resource["clinicalStatus"] is null ||
            resource["verificationStatus"] is not JsonObject verification ||
            verification["coding"] is not JsonArray coding)
        {
            return false;
        }

        var enteredInError = coding
            .OfType<JsonObject>()
            .Any(c => string.Equals(c["code"]?.GetValue<string>(), "entered-in-error", StringComparison.OrdinalIgnoreCase));

        if (!enteredInError)
        {
            return false;
        }

        resource.Remove("clinicalStatus");
        return true;
    }

    private static bool SanitizeStrings(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
            {
                var changed = false;
                // Snapshot the keys first — we reassign values while walking.
                foreach (var key in obj.Select(kvp => kvp.Key).ToList())
                {
                    changed |= SanitizeChild(obj[key], value => obj[key] = value);
                }

                return changed;
            }

            case JsonArray arr:
            {
                var changed = false;
                for (var i = 0; i < arr.Count; i++)
                {
                    var index = i;
                    changed |= SanitizeChild(arr[index], value => arr[index] = value);
                }

                return changed;
            }

            default:
                return false;
        }
    }

    private static bool SanitizeChild(JsonNode? child, Action<string> assign)
    {
        if (child is JsonValue value && value.TryGetValue<string>(out var s))
        {
            var cleaned = CleanString(s);
            if (!ReferenceEquals(cleaned, s))
            {
                assign(cleaned);
                return true;
            }

            return false;
        }

        return SanitizeStrings(child);
    }

    // Drops disallowed control characters, then trims surrounding whitespace. Returns the SAME instance when nothing
    // changed, so callers can skip re-serialization for the (overwhelmingly common) clean case — important because
    // this walks every string, including large base64 Binary.data payloads.
    private static string CleanString(string value)
    {
        if (value.Length == 0)
        {
            return value;
        }

        var hasDisallowed = value.Any(IsDisallowedControl);
        var needsTrim = char.IsWhiteSpace(value[0]) || char.IsWhiteSpace(value[^1]);
        if (!hasDisallowed && !needsTrim)
        {
            return value;
        }

        var builder = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (!IsDisallowedControl(ch))
            {
                builder.Append(ch);
            }
        }

        return builder.ToString().Trim();
    }

    private static bool IsDisallowedControl(char ch)
        => (ch < ' ' && ch != '\t' && ch != '\n' && ch != '\r') || ch == (char)0x7F;
}

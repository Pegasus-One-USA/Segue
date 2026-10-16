using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace FHIRBridge.Infrastructure.Cds.Handlers;

/// <summary>One coding of a CodeableConcept. Every member may be null.</summary>
internal sealed record FhirCode(string? System, string? Code, string? Display);

/// <summary>
/// Lenient readers over raw FHIR JSON (context and prefetch). Every method returns null or empty for anything
/// missing or of the wrong shape and never throws, so a handler answers "no card" rather than failing on an
/// unexpected payload. Accepts R4 and the R5 spellings the handlers care about.
/// </summary>
internal static class CdsFhirReader
{
    public static string? Text(JsonNode? node) =>
        node is JsonValue value && value.GetValueKind() == JsonValueKind.String && value.TryGetValue(out string? text)
            ? text
            : null;

    /// <summary>A JSON number of any backing type (TryGetValue&lt;double&gt; alone misses an int-backed node).</summary>
    public static double? Number(JsonNode? node) =>
        node is JsonValue value
        && value.GetValueKind() == JsonValueKind.Number
        && double.TryParse(value.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
            ? number
            : null;

    public static string? ResourceType(JsonObject resource) => Text(resource["resourceType"]);

    /// <summary>The resources in a Bundle's entries, or the node itself when it is a single resource.</summary>
    public static IReadOnlyList<JsonObject> Resources(JsonNode? node)
    {
        if (node is not JsonObject resource || ResourceType(resource) is not { } resourceType)
        {
            return [];
        }

        if (resourceType != "Bundle")
        {
            return [resource];
        }

        if (resource["entry"] is not JsonArray entries)
        {
            return [];
        }

        return entries
            .OfType<JsonObject>()
            .Select(entry => entry["resource"] as JsonObject)
            .Where(item => item is not null && ResourceType(item) is not null)
            .Cast<JsonObject>()
            .ToArray();
    }

    public static IReadOnlyList<JsonObject> Resources(JsonNode? node, string resourceType) =>
        Resources(node).Where(resource => ResourceType(resource) == resourceType).ToArray();

    /// <summary>The codings of a CodeableConcept (or an R5 CodeableReference's concept).</summary>
    public static IReadOnlyList<FhirCode> Codings(JsonNode? concept)
    {
        if (concept is not JsonObject conceptObject)
        {
            return [];
        }

        if (conceptObject["concept"] is JsonObject inner)
        {
            conceptObject = inner;
        }

        var codings = conceptObject["coding"] is JsonArray array
            ? array.OfType<JsonObject>()
                .Select(coding => new FhirCode(Text(coding["system"]), Text(coding["code"]), Text(coding["display"])))
                .Where(coding => coding.Code is not null || coding.Display is not null)
                .ToList()
            : [];

        if (Text(conceptObject["text"]) is { Length: > 0 } text)
        {
            codings.Add(new FhirCode(null, null, text));
        }

        return codings;
    }

    /// <summary>
    /// The medication concept of a MedicationRequest: R4 medicationCodeableConcept, R5 medication.concept, or a
    /// medicationReference (Epic since Feb 2024) resolved to its Medication's code — a contained Medication, or one
    /// found in <paramref name="medications"/> (bundle entries of draftOrders / prefetch). When the Medication cannot
    /// be found, the reference's display text alone is used (matched by name).
    /// </summary>
    public static JsonNode? MedicationConcept(JsonObject resource, CdsMedicationIndex? medications = null)
    {
        var medication = resource["medication"] as JsonObject;
        if ((resource["medicationCodeableConcept"] ?? medication?["concept"]) is { } concept)
        {
            return concept;
        }

        var reference = resource["medicationReference"] as JsonObject ?? medication?["reference"] as JsonObject;
        if (reference is null)
        {
            return null;
        }

        var resolved = ResolveMedication(resource, Text(reference["reference"]), medications);
        return ReferenceConcept(resolved?["code"] as JsonObject, Text(reference["display"]));
    }

    /// <summary>
    /// The ordered item's concept of any draft order: a medication, or the code of a ServiceRequest / (STU3)
    /// ProcedureRequest / DeviceRequest (an R5 CodeableReference is unwrapped by <see cref="Codings"/>).
    /// </summary>
    public static JsonNode? OrderConcept(JsonObject resource, CdsMedicationIndex? medications = null) =>
        MedicationConcept(resource, medications) ?? resource["code"] ?? resource["codeCodeableConcept"];

    /// <summary>
    /// The Medication a reference names: "#id" in the resource's contained list; otherwise the indexed Medication
    /// ("Medication/id" or a full URL), else a contained Medication with that id, else the only contained Medication.
    /// </summary>
    private static JsonObject? ResolveMedication(JsonObject resource, string? reference, CdsMedicationIndex? medications)
    {
        var contained = resource["contained"] is JsonArray array
            ? array.OfType<JsonObject>().Where(item => ResourceType(item) == "Medication").ToArray()
            : [];

        if (reference is { Length: > 1 } && reference[0] == '#')
        {
            return contained.FirstOrDefault(item => Text(item["id"]) == reference[1..]);
        }

        if (reference is not null)
        {
            if (medications?.Find(reference) is { } indexed)
            {
                return indexed;
            }

            var id = reference.TrimEnd('/');
            id = id[(id.LastIndexOf('/') + 1)..];
            if (contained.FirstOrDefault(item => Text(item["id"]) == id) is { } byId)
            {
                return byId;
            }
        }

        return contained.Length == 1 ? contained[0] : null;
    }

    /// <summary>A concept built from a Medication's code plus the reference's display (either may be missing).</summary>
    private static JsonObject? ReferenceConcept(JsonObject? code, string? display)
    {
        var codings = code?["coding"] is JsonArray array
            ? array.OfType<JsonObject>().Select(coding => (JsonNode?)coding.DeepClone()).ToList()
            : [];
        var text = Text(code?["text"]);
        if (!string.IsNullOrWhiteSpace(display) && text is not null && !string.Equals(text, display, StringComparison.OrdinalIgnoreCase))
        {
            codings.Add(new JsonObject { ["display"] = display });
        }

        text ??= string.IsNullOrWhiteSpace(display) ? null : display;
        if (codings.Count == 0 && text is null)
        {
            return null;
        }

        var concept = new JsonObject { ["coding"] = new JsonArray(codings.ToArray()) };
        if (text is not null)
        {
            concept["text"] = text;
        }

        return concept;
    }

    /// <summary>
    /// True for resource types that can be a draft order (MedicationRequest, ServiceRequest, STU3 ProcedureRequest
    /// and ReferralRequest, DeviceRequest, NutritionOrder, VisionPrescription, ...) — not the Medication entries Epic
    /// includes beside its MedicationRequests in draftOrders.
    /// </summary>
    public static bool IsOrderType(string? resourceType) =>
        resourceType is not null
        && (resourceType.EndsWith("Request", StringComparison.Ordinal)
            || resourceType is "NutritionOrder" or "VisionPrescription" or "MedicationOrder");

    /// <summary>A short human label for a concept: its text, else the first display, else the first code.</summary>
    public static string Label(JsonNode? concept, string fallback)
    {
        var codings = Codings(concept);
        return codings.LastOrDefault(c => c.System is null && c.Display is not null)?.Display
            ?? codings.FirstOrDefault(c => c.Display is not null)?.Display
            ?? codings.FirstOrDefault(c => c.Code is not null)?.Code
            ?? fallback;
    }

    /// <summary>All lower-cased words of every display/text in the concept, joined, for name matching.</summary>
    public static string SearchText(JsonNode? concept) =>
        string.Join(" | ", Codings(concept).Select(c => c.Display).Where(d => d is not null)).ToLowerInvariant();

    /// <summary>
    /// True when <paramref name="text"/> (lower case) contains <paramref name="term"/> (lower case) as a whole
    /// word or phrase, so "aspirin" does not match "aspirinate" and "ace" does not match "space".
    /// </summary>
    public static bool ContainsTerm(string text, string term)
    {
        if (term.Length == 0)
        {
            return false;
        }

        var start = 0;
        while ((start = text.IndexOf(term, start, StringComparison.Ordinal)) >= 0)
        {
            var before = start == 0 || !char.IsLetterOrDigit(text[start - 1]);
            var end = start + term.Length;
            var after = end >= text.Length || !char.IsLetterOrDigit(text[end]);
            if (before && after)
            {
                return true;
            }

            start = end;
        }

        return false;
    }

    /// <summary>True when any coding's code equals one of <paramref name="codes"/> (system ignored unless given as "system|code").</summary>
    public static bool HasCode(JsonNode? concept, IEnumerable<string> codes)
    {
        var codings = Codings(concept).Where(c => c.Code is not null).ToArray();
        if (codings.Length == 0)
        {
            return false;
        }

        foreach (var entry in codes)
        {
            var separator = entry.LastIndexOf('|');
            var system = separator > 0 ? entry[..separator] : null;
            var code = separator >= 0 ? entry[(separator + 1)..] : entry;
            if (codings.Any(c =>
                    string.Equals(c.Code, code, StringComparison.OrdinalIgnoreCase)
                    && (system is null || string.Equals(c.System, system, StringComparison.OrdinalIgnoreCase))))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Parses a FHIR date/dateTime/instant ("2024", "2024-03", "2024-03-01", "2024-03-01T10:00:00Z").</summary>
    public static DateTimeOffset? Date(JsonNode? node)
    {
        var text = Text(node);
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        string[] partialFormats = ["yyyy", "yyyy-MM", "yyyy-MM-dd"];
        if (DateTime.TryParseExact(text, partialFormats, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var partial))
        {
            return new DateTimeOffset(partial, TimeSpan.Zero);
        }

        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var full)
            ? full
            : null;
    }

    /// <summary>When an Observation/Procedure/Immunization happened: effective/performed/occurrence, then issued/recorded.</summary>
    public static DateTimeOffset? ClinicalDate(JsonObject resource) =>
        Date(resource["effectiveDateTime"])
        ?? Date((resource["effectivePeriod"] as JsonObject)?["end"])
        ?? Date((resource["effectivePeriod"] as JsonObject)?["start"])
        ?? Date(resource["effectiveInstant"])
        ?? Date(resource["performedDateTime"])
        ?? Date((resource["performedPeriod"] as JsonObject)?["end"])
        ?? Date((resource["performedPeriod"] as JsonObject)?["start"])
        ?? Date(resource["occurrenceDateTime"])
        ?? Date(resource["issued"])
        ?? Date(resource["recorded"])
        ?? Date(resource["recordedDate"]);

    /// <summary>A status-like code from a plain code element or a CodeableConcept (e.g. clinicalStatus).</summary>
    public static string? StatusCode(JsonNode? node) =>
        Text(node) ?? Codings(node).FirstOrDefault(c => c.Code is not null)?.Code;
}

/// <summary>
/// The Medication resources a request carries as Bundle entries (in context.draftOrders or a prefetch Bundle), by
/// "Medication/id" and by entry fullUrl, so a MedicationRequest's medicationReference can be resolved.
/// </summary>
public sealed class CdsMedicationIndex
{
    private readonly Dictionary<string, JsonObject> _byReference = new(StringComparer.Ordinal);

    private CdsMedicationIndex()
    {
    }

    public static CdsMedicationIndex Empty { get; } = new();

    public int Count => _byReference.Count;

    public static CdsMedicationIndex Build(IEnumerable<JsonNode?> nodes)
    {
        var index = new CdsMedicationIndex();
        foreach (var node in nodes)
        {
            if (node is not JsonObject root)
            {
                continue;
            }

            if (CdsFhirReader.ResourceType(root) == "Medication")
            {
                index.Add(root, null);
                continue;
            }

            if (CdsFhirReader.ResourceType(root) != "Bundle" || root["entry"] is not JsonArray entries)
            {
                continue;
            }

            foreach (var entry in entries.OfType<JsonObject>())
            {
                if (entry["resource"] is JsonObject resource && CdsFhirReader.ResourceType(resource) == "Medication")
                {
                    index.Add(resource, CdsFhirReader.Text(entry["fullUrl"]));
                }
            }
        }

        return index;
    }

    /// <summary>The Medication a reference names: exact, else its trailing "Medication/id" (for an absolute URL).</summary>
    public JsonObject? Find(string reference)
    {
        var trimmed = reference.Trim();
        if (_byReference.TryGetValue(trimmed, out var exact))
        {
            return exact;
        }

        var marker = trimmed.LastIndexOf("Medication/", StringComparison.Ordinal);
        return marker > 0 && _byReference.TryGetValue(trimmed[marker..], out var relative) ? relative : null;
    }

    private void Add(JsonObject medication, string? fullUrl)
    {
        if (CdsFhirReader.Text(medication["id"]) is { Length: > 0 } id)
        {
            _byReference.TryAdd($"Medication/{id}", medication);
        }

        if (!string.IsNullOrWhiteSpace(fullUrl))
        {
            _byReference.TryAdd(fullUrl.Trim(), medication);
        }
    }
}

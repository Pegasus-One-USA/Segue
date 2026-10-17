using System.Text.Json.Nodes;

namespace FHIRBridge.Infrastructure.Destinations.EhrWriteBack;

/// <summary>Small JsonNode helpers shared by the write profiles. Every getter tolerates a missing or wrongly typed
/// element and returns null, because source resources come from many EHRs of uneven quality.</summary>
internal static class EhrFhirJson
{
    public const string LoincSystem = "http://loinc.org";
    public const string SnomedSystem = "http://snomed.info/sct";
    public const string Icd10CmSystem = "http://hl7.org/fhir/sid/icd-10-cm";
    public const string RxNormSystem = "http://www.nlm.nih.gov/research/umls/rxnorm";
    public const string NdcSystem = "http://hl7.org/fhir/sid/ndc";
    public const string CvxSystem = "http://hl7.org/fhir/sid/cvx";
    public const string UcumSystem = "http://unitsofmeasure.org";

    /// <summary>The first code of the given system in a CodeableConcept, or null.</summary>
    public static string? CodeOf(JsonNode? codeableConcept, string system) =>
        Objects(codeableConcept, "coding")
            .Where(c => string.Equals(String(c, "system"), system, StringComparison.OrdinalIgnoreCase))
            .Select(c => String(c, "code"))
            .FirstOrDefault(c => !string.IsNullOrWhiteSpace(c));

    /// <summary>A CodeableConcept's human name: its text, else the first coding display.</summary>
    public static string? DisplayName(JsonNode? codeableConcept) =>
        String(codeableConcept, "text") is { Length: > 0 } text
            ? text
            : Objects(codeableConcept, "coding").Select(c => String(c, "display")).FirstOrDefault(d => !string.IsNullOrWhiteSpace(d));

    /// <summary>The year, month and day of a FHIR date or dateTime ("2024", "2024-05", "2024-05-01T10:00:00Z"), each
    /// null when absent; null altogether when the text is not a FHIR date.</summary>
    public static (int Year, int? Month, int? Day)? DateParts(string? fhirDate)
    {
        if (string.IsNullOrWhiteSpace(fhirDate) || fhirDate.Length < 4 || !int.TryParse(fhirDate[..4], out var year))
        {
            return null;
        }

        int? month = fhirDate.Length >= 7 && fhirDate[4] == '-' && int.TryParse(fhirDate[5..7], out var m) ? m : null;
        int? day = month is not null && fhirDate.Length >= 10 && fhirDate[7] == '-' && int.TryParse(fhirDate[8..10], out var d) ? d : null;
        return (year, month, day);
    }

    public static string? String(JsonNode? node, string property) =>
        node is JsonObject obj && obj[property] is JsonValue value && value.TryGetValue<string>(out var text)
            ? text
            : null;

    public static JsonObject? Object(JsonNode? node, string property) => (node as JsonObject)?[property] as JsonObject;

    public static JsonArray? Array(JsonNode? node, string property) => (node as JsonObject)?[property] as JsonArray;

    public static IEnumerable<JsonObject> Objects(JsonNode? node, string property) =>
        Array(node, property)?.OfType<JsonObject>() ?? [];

    /// <summary>Codes of every coding of a CodeableConcept.</summary>
    public static IEnumerable<string> Codes(JsonNode? codeableConcept) =>
        Objects(codeableConcept, "coding").Select(coding => String(coding, "code")).OfType<string>();

    /// <summary>The first code of a CodeableConcept property, e.g. clinicalStatus.</summary>
    public static string? FirstCode(JsonNode? resource, string property) => Codes(Object(resource, property)).FirstOrDefault();

    public static bool HasCoding(JsonNode? codeableConcept, string system, string code) =>
        Objects(codeableConcept, "coding").Any(coding =>
            string.Equals(String(coding, "system"), system, StringComparison.OrdinalIgnoreCase)
            && string.Equals(String(coding, "code"), code, StringComparison.Ordinal));

    public static JsonObject CodeableConcept(string system, string code, string? display = null)
    {
        var coding = new JsonObject { ["system"] = system, ["code"] = code };
        if (display is not null)
        {
            coding["display"] = display;
        }

        return new JsonObject { ["coding"] = new JsonArray(coding) };
    }

    /// <summary>A copy of a CodeableConcept keeping only codings that carry a code, plus its text.</summary>
    public static JsonObject? CopyCodedConcept(JsonNode? source, Func<JsonObject, bool>? keepCoding = null)
    {
        if (source is not JsonObject concept)
        {
            return null;
        }

        var codings = new JsonArray();
        foreach (var coding in Objects(concept, "coding"))
        {
            var code = String(coding, "code");
            if (string.IsNullOrWhiteSpace(code) || (keepCoding is not null && !keepCoding(coding)))
            {
                continue;
            }

            var copy = new JsonObject();
            CopyIfString(coding, copy, "system");
            copy["code"] = code;
            CopyIfString(coding, copy, "display");
            codings.Add(copy);
        }

        var text = String(concept, "text");
        if (codings.Count == 0 && string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var result = new JsonObject();
        if (codings.Count > 0)
        {
            result["coding"] = codings;
        }

        if (!string.IsNullOrWhiteSpace(text))
        {
            result["text"] = text;
        }

        return result;
    }

    public static void CopyIfString(JsonNode? source, JsonObject target, string property, string? targetProperty = null)
    {
        if (String(source, property) is { Length: > 0 } value)
        {
            target[targetProperty ?? property] = value;
        }
    }

    public static void CopyIfPresent(JsonNode? source, JsonObject target, string property)
    {
        if ((source as JsonObject)?[property] is { } value)
        {
            target[property] = value.DeepClone();
        }
    }

    /// <summary>The <c>reference</c> string of a Reference-typed property, e.g. <c>subject</c>.</summary>
    public static string? Reference(JsonNode? resource, string property) => String(Object(resource, property), "reference");

    /// <summary>Concatenated <c>note[].text</c>, trimmed to <paramref name="maxLength"/>.</summary>
    public static string? NoteText(JsonNode? resource, int maxLength)
    {
        var text = string.Join(
            "\n",
            Objects(resource, "note").Select(note => String(note, "text")).Where(t => !string.IsNullOrWhiteSpace(t)));
        if (text.Length == 0)
        {
            return null;
        }

        return text.Length <= maxLength ? text : text[..maxLength];
    }

    public static JsonArray SingleNote(string text) => new(new JsonObject { ["text"] = text });

    public static JsonObject ReferenceTo(string resourceType, string id) => new() { ["reference"] = $"{resourceType}/{id}" };
}

/// <summary>Parses FHIR literal references: <c>Type/id</c>, <c>Type/id/_history/v</c>, or an absolute URL ending in
/// one. Contained (<c>#</c>) and <c>urn:</c> references are not resolvable against an EHR and return false.</summary>
internal static class EhrFhirReference
{
    public static bool TryParse(string? reference, out string resourceType, out string id)
    {
        resourceType = string.Empty;
        id = string.Empty;
        if (string.IsNullOrWhiteSpace(reference) || reference.StartsWith('#') || reference.StartsWith("urn:", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var path = reference.Trim();
        if (Uri.TryCreate(path, UriKind.Absolute, out var absolute) && absolute.Scheme is "http" or "https")
        {
            path = absolute.AbsolutePath;
        }

        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var historyIndex = System.Array.IndexOf(segments, "_history");
        var end = historyIndex >= 0 ? historyIndex : segments.Length;
        if (end < 2)
        {
            return false;
        }

        resourceType = segments[end - 2];
        id = Uri.UnescapeDataString(segments[end - 1]);
        return resourceType.Length > 0 && char.IsUpper(resourceType[0]) && id.Length > 0;
    }
}

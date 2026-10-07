using System.Text;
using System.Text.Json.Nodes;
using static FHIRBridge.Infrastructure.Destinations.EhrWriteBack.EhrFhirJson;

namespace FHIRBridge.Infrastructure.Destinations.EhrWriteBack;

/// <summary>
/// A source clinical note reduced to what a vendor note API is built from: its patient, type, date, source encounter
/// and plain text. Shared by the note profiles of vendors that do not take a FHIR DocumentReference as such
/// (eClinicalWorks wraps the text in an HL7 v2 message, athenaOne posts it as a chart document); Epic's own profile
/// keeps its stricter rules.
/// </summary>
internal static class EhrClinicalNote
{
    /// <param name="Subject">The source patient reference.</param>
    /// <param name="Text">Plain text, converted from HTML or RTF when the source sent those.</param>
    /// <param name="Type">The note type, LOINC codings only when there are any.</param>
    /// <param name="LoincCode">The first LOINC code of the type, if any.</param>
    /// <param name="Date">The note's <c>date</c>, as sent.</param>
    /// <param name="SourceEncounter">The source encounter reference, if any.</param>
    public sealed record Note(
        string Subject,
        string Text,
        JsonObject Type,
        string? LoincCode,
        string? Date,
        string? SourceEncounter);

    /// <summary>The note, or the skip/reject that stops it. <paramref name="excludedLoincTypes"/> are note types the
    /// vendor files through another workflow.</summary>
    public static (Note? Note, EhrShapeResult? Problem) Parse(JsonObject source, IReadOnlySet<string> excludedLoincTypes)
    {
        var status = String(source, "status");
        if (status is "superseded" or "entered-in-error")
        {
            return (null, EhrShapeResult.Skip(status));
        }

        var categories = Objects(source, "category").ToList();
        if (categories.Count > 0 && !categories.Any(c => Codes(c).Contains("clinical-note")))
        {
            return (null, EhrShapeResult.Skip("not-a-clinical-note"));
        }

        var subject = Reference(source, "subject");
        if (subject is null)
        {
            return (null, EhrShapeResult.Reject("missing-patient"));
        }

        var typeSource = Object(source, "type");
        var loinc = Objects(typeSource, "coding")
            .Where(c => String(c, "system") == LoincSystem)
            .Select(c => String(c, "code"))
            .FirstOrDefault(c => c is not null);
        if (loinc is not null && excludedLoincTypes.Contains(loinc))
        {
            return (null, EhrShapeResult.Skip("excluded-note-type"));
        }

        if (IsCcdaDocument(source))
        {
            return (null, EhrShapeResult.Skip("ccda-document"));
        }

        var loincOnly = CopyCodedConcept(typeSource, coding => String(coding, "system") == LoincSystem);
        var type = loincOnly?["coding"] is JsonArray { Count: > 0 } ? loincOnly : CopyCodedConcept(typeSource);
        if (type?["coding"] is not JsonArray { Count: > 0 })
        {
            return (null, EhrShapeResult.Reject("missing-note-type"));
        }

        var attachment = Objects(source, "content").Select(c => Object(c, "attachment")).FirstOrDefault(a => a is not null);
        if (attachment is null)
        {
            return (null, EhrShapeResult.Reject("missing-note-content"));
        }

        var contentType = String(attachment, "contentType")?.Split(';')[0].Trim().ToLowerInvariant();
        var data = String(attachment, "data");
        if (string.IsNullOrWhiteSpace(data))
        {
            return (null, EhrShapeResult.Reject("note-content-not-inline"));
        }

        if (!EhrNoteText.CanConvert(contentType))
        {
            return (null, EhrShapeResult.Reject("note-format-not-supported"));
        }

        string decoded;
        try
        {
            decoded = Encoding.UTF8.GetString(Convert.FromBase64String(data));
        }
        catch (FormatException)
        {
            return (null, EhrShapeResult.Reject("note-content-not-base64"));
        }

        var text = contentType == "text/plain" ? decoded : EhrNoteText.ToPlainText(contentType!, decoded);
        if (string.IsNullOrWhiteSpace(text))
        {
            return (null, EhrShapeResult.Reject("note-empty-after-conversion"));
        }

        var encounter = Objects(Object(source, "context"), "encounter")
            .Select(e => String(e, "reference"))
            .FirstOrDefault(r => r is not null);

        return (new Note(subject, text, type, loinc, String(source, "date"), encounter), null);
    }

    /// <summary>A C-CDA document (format from the SDWG C-CDA family, or LOINC 34133-9): a whole-chart summary, not a
    /// note.</summary>
    public static bool IsCcdaDocument(JsonObject source) =>
        Objects(source, "content").Any(c => String(Object(c, "format"), "code")?.StartsWith("urn:hl7-org:sdwg:ccda", StringComparison.OrdinalIgnoreCase) == true)
        || Objects(Object(source, "type"), "coding").Any(c => String(c, "system") == LoincSystem && String(c, "code") == "34133-9");
}

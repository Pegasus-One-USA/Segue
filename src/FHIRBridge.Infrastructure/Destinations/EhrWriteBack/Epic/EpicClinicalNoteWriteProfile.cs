using System.Text;
using System.Text.Json.Nodes;
using FHIRBridge.Application.Abstractions.Destinations;
using FHIRBridge.Domain.Enums;
using static FHIRBridge.Infrastructure.Destinations.EhrWriteBack.EhrFhirJson;

namespace FHIRBridge.Infrastructure.Destinations.EhrWriteBack.Epic;

/// <summary>
/// Epic DocumentReference.Create (Clinical Notes), spec 1046. Files to the chart against an existing encounter of the
/// same patient (the sandbox accepted a finished stay). Epic saves only the first attachment, which must be
/// base64 plain text, so HTML and RTF notes are converted (<see cref="EhrNoteText"/>); <c>status</c> must be current. With no author Epic files the note under the background user.
/// It does not deduplicate.
/// </summary>
public sealed class EpicClinicalNoteWriteProfile : IEhrWriteProfile
{
    private const string CategorySystem = "http://hl7.org/fhir/us/core/CodeSystem/us-core-documentreference-category";

    // Discharge summaries and patient instructions are filed through other Epic workflows, not as clinical notes.
    private static readonly HashSet<string> ExcludedLoincTypes = new(StringComparer.Ordinal) { "18842-5", "34105-7", "69730-0" };

    public SourceSystemType Vendor => SourceSystemType.Epic;

    public string ResourceType => "DocumentReference";

    public string? Variant => FHIRBridge.Domain.Fhir.EhrWriteVariants.ClinicalNote;

    public EhrShapeResult Shape(JsonObject source, EhrWriteBackRunOptions options)
    {
        var status = String(source, "status");
        if (status is "superseded" or "entered-in-error")
        {
            return EhrShapeResult.Skip(status == "superseded" ? "superseded" : "entered-in-error");
        }

        // Scanned documents and other document metadata go through different Epic APIs.
        var categories = Objects(source, "category").ToList();
        if (categories.Count > 0 && !categories.Any(c => Codes(c).Contains("clinical-note")))
        {
            return EhrShapeResult.Skip("not-a-clinical-note");
        }

        var subject = Reference(source, "subject");
        if (subject is null)
        {
            return EhrShapeResult.Reject("missing-patient");
        }

        var typeSource = Object(source, "type");
        if (Objects(typeSource, "coding").Any(c => String(c, "system") == LoincSystem && String(c, "code") is { } code && ExcludedLoincTypes.Contains(code)))
        {
            return EhrShapeResult.Skip("excluded-note-type");
        }

        // A C-CDA document (eCW serves its Continuity of Care Document as a DocumentReference) is a structured summary
        // of the whole chart, not a note: flattened into a progress note it would bury the chart in tens of kilobytes
        // of text. EHRs take these through document exchange, not the clinical-notes API. A skip, not a rejection:
        // nothing is wrong with the record.
        if (IsCcdaDocument(source))
        {
            return EhrShapeResult.Skip("ccda-document");
        }

        var loincOnly = CopyCodedConcept(typeSource, coding => String(coding, "system") == LoincSystem);
        var type = loincOnly?["coding"] is JsonArray { Count: > 0 } ? loincOnly : CopyCodedConcept(typeSource);
        if (type?["coding"] is not JsonArray { Count: > 0 })
        {
            return EhrShapeResult.Reject("missing-note-type");
        }

        var attachment = Objects(source, "content").Select(c => Object(c, "attachment")).FirstOrDefault(a => a is not null);
        if (attachment is null)
        {
            return EhrShapeResult.Reject("missing-note-content");
        }

        var contentType = String(attachment, "contentType")?.Split(';')[0].Trim().ToLowerInvariant();
        var data = String(attachment, "data");
        if (string.IsNullOrWhiteSpace(data))
        {
            // The writer reads a source Binary into the note before shaping (EhrNoteContent); a note still without
            // data here has a link the writer could not follow.
            return EhrShapeResult.Reject("note-content-not-inline");
        }

        // Epic takes base64 plain text only: plain text is sent as it came, HTML and RTF are converted first.
        if (!EhrNoteText.CanConvert(contentType))
        {
            return EhrShapeResult.Reject("note-format-not-supported");
        }

        if (contentType != "text/plain")
        {
            string decoded;
            try
            {
                decoded = Encoding.UTF8.GetString(Convert.FromBase64String(data));
            }
            catch (FormatException)
            {
                return EhrShapeResult.Reject("note-content-not-base64");
            }

            var plain = EhrNoteText.ToPlainText(contentType!, decoded);
            if (string.IsNullOrWhiteSpace(plain))
            {
                return EhrShapeResult.Reject("note-empty-after-conversion");
            }

            data = Convert.ToBase64String(Encoding.UTF8.GetBytes(plain));
        }

        var docStatus = options.NoteDocStatus == EhrWriteBackRunOptions.FinalDocStatus
            ? EhrWriteBackRunOptions.FinalDocStatus
            : EhrWriteBackRunOptions.PreliminaryDocStatus;

        var shaped = new JsonObject
        {
            ["resourceType"] = ResourceType,
            ["status"] = "current",
            ["docStatus"] = docStatus,
            ["type"] = type,
            ["category"] = new JsonArray(CodeableConcept(CategorySystem, "clinical-note", "Clinical Note")),
            ["subject"] = new JsonObject { ["reference"] = subject },
        };
        CopyIfString(source, shaped, "date");
        shaped["content"] = new JsonArray(new JsonObject
        {
            ["attachment"] = new JsonObject { ["contentType"] = "text/plain", ["data"] = data },
        });

        var encounter = Objects(Object(source, "context"), "encounter")
            .Select(e => String(e, "reference"))
            .FirstOrDefault(r => r is not null);

        return EhrShapeResult.Shaped(shaped, subject, encounter);
    }

    /// <summary>
    /// A C-CDA document: content <c>format</c> from the HL7 SDWG C-CDA family (urn:hl7-org:sdwg:ccda-…), or a
    /// Summarization of Episode note (LOINC 34133-9, the CCD's document type).
    /// </summary>
    private static bool IsCcdaDocument(JsonObject source) =>
        Objects(source, "content").Any(c => String(Object(c, "format"), "code")?.StartsWith("urn:hl7-org:sdwg:ccda", StringComparison.OrdinalIgnoreCase) == true)
        || Objects(Object(source, "type"), "coding").Any(c => String(c, "system") == LoincSystem && String(c, "code") == "34133-9");

    public void BindReferences(JsonObject shaped, string targetPatientId, string? targetEncounterId)
    {
        shaped["subject"] = ReferenceTo("Patient", targetPatientId);
        if (targetEncounterId is not null)
        {
            shaped["context"] = new JsonObject
            {
                ["encounter"] = new JsonArray(ReferenceTo("Encounter", targetEncounterId)),
            };
        }
    }
}

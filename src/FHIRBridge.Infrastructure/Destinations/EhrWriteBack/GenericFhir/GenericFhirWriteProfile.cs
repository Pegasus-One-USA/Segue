using System.Text.Json.Nodes;
using FHIRBridge.Application.Abstractions.Destinations;
using FHIRBridge.Domain.Enums;
using FHIRBridge.Domain.Fhir;
using static FHIRBridge.Infrastructure.Destinations.EhrWriteBack.EhrFhirJson;

namespace FHIRBridge.Infrastructure.Destinations.EhrWriteBack.GenericFhir;

/// <summary>
/// A plain FHIR R4 create on a Generic FHIR server: the source resource as it is, minus what belongs to the source
/// server. One instance per type in <see cref="EhrWriteCapabilities.GenericFhirResourceTypes"/>.
///
/// <list type="bullet">
/// <item><c>id</c>, <c>meta</c> (except declared profiles) and narrative <c>text</c> go: the server assigns its own,
/// and a narrative may describe what the shaping removed.</item>
/// <item>The patient reference is bound to the server's patient by the writer; the record's encounter is not (a
/// Generic server's encounters are not resolved), so it is removed.</item>
/// <item>Every other literal reference (a practitioner, an organisation, a medication, a plan) names a SOURCE record
/// that the server does not have; a server that checks references would refuse the whole record, and one that does not
/// would hold a dangling link. Each keeps its <c>display</c> and <c>identifier</c> and loses its <c>reference</c>; one
/// left with nothing gets a display saying it was not transferred. Contained (<c>#</c>) references stay.</item>
/// <item>An attachment's <c>url</c> that names a FHIR resource (relative <c>Binary/123</c>, or absolute into a FHIR
/// server) goes too, its embedded <c>data</c> stays. A record whose content IS its attachments (a note, a message
/// payload, an attachment value, a report with nothing but its presentedForm) and whose every such attachment is left
/// without content is skipped as <c>attachment-not-transferable</c>; an incidental one (a patient's photo, a report's
/// rendering beside its results) only loses its link. A note's first attachment is the exception: the writer brings
/// its text inline from the source first.</item>
/// </list>
/// </summary>
public sealed class GenericFhirWriteProfile : IEhrWriteProfile
{
    // Where each type keeps its patient; every other type uses subject.
    private static readonly IReadOnlyDictionary<string, string> PatientProperties = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["AllergyIntolerance"] = "patient",
        ["BodyStructure"] = "patient",
        ["Immunization"] = "patient",
    };

    private readonly string _patientProperty;

    public GenericFhirWriteProfile(string resourceType)
    {
        ResourceType = resourceType;
        _patientProperty = PatientProperties.GetValueOrDefault(resourceType, "subject");
    }

    public SourceSystemType Vendor => SourceSystemType.GenericFhir;

    public string ResourceType { get; }

    public EhrShapeResult Shape(JsonObject source, EhrWriteBackRunOptions options)
    {
        if (String(source, "status") == "entered-in-error"
            || Codes(Object(source, "verificationStatus")).Contains("entered-in-error"))
        {
            return EhrShapeResult.Skip("entered-in-error");
        }

        var shaped = new JsonObject();
        var attachments = new AttachmentTally
        {
            // A report's presentedForm is its content only when it has no results or conclusion of its own.
            PresentedFormIsContent = source["result"] is null && source["conclusion"] is null && source["conclusionCode"] is null,
        };
        string? patientReference = null;
        string? encounterReference = null;
        foreach (var (name, value) in source)
        {
            if (name is "id" or "text" or "meta" || value is null)
            {
                continue;
            }

            if (ResourceType != "Patient" && name == _patientProperty)
            {
                patientReference = String(value, "reference");
                shaped[name] = value.DeepClone();
                continue;
            }

            if (name == "encounter")
            {
                encounterReference = String(value, "reference");
                continue;
            }

            // A DocumentReference's context holds its encounter as well as its period: the encounter goes.
            var copy = value.DeepClone();
            if (ResourceType == "DocumentReference" && name == "content")
            {
                attachments.NoteAttachment = FirstNoteAttachment(copy);
            }

            DetachReferences(copy, isContextOfDocument: name == "context", attachments, name);
            shaped[name] = copy;
        }

        if (Array(Object(source, "meta"), "profile") is { Count: > 0 } profiles)
        {
            shaped["meta"] = new JsonObject { ["profile"] = profiles.DeepClone() };
        }

        shaped["resourceType"] = ResourceType;
        if (ResourceType != "Patient" && patientReference is null)
        {
            return EhrShapeResult.Reject("missing-patient");
        }

        if (attachments.Emptied > 0 && attachments.Kept == 0)
        {
            // Every attachment that IS the record's content was a link the server cannot follow: the record would
            // arrive empty. An incidental one (a patient's photo, a report's rendering beside its results) just loses
            // its link.
            return EhrShapeResult.Skip("attachment-not-transferable");
        }

        return EhrShapeResult.Shaped(Ordered(shaped), patientReference, encounterReference);
    }

    public void BindReferences(JsonObject shaped, string targetPatientId, string? targetEncounterId)
    {
        if (ResourceType != "Patient")
        {
            shaped[_patientProperty] = ReferenceTo("Patient", targetPatientId);
        }
    }

    /// <summary>resourceType first, the rest in source order, so the content hash is stable.</summary>
    private static JsonObject Ordered(JsonObject shaped)
    {
        var ordered = new JsonObject { ["resourceType"] = shaped["resourceType"]!.DeepClone() };
        foreach (var (name, value) in shaped)
        {
            if (name != "resourceType")
            {
                ordered[name] = value?.DeepClone();
            }
        }

        return ordered;
    }

    // The R4 elements of type Attachment in the types this profile writes (DocumentReference.content.attachment,
    // DiagnosticReport.presentedForm, Communication.payload.contentAttachment, QuestionnaireResponse answers, Patient
    // photo). An extension's own "url" is never one of these.
    private static readonly HashSet<string> AttachmentProperties = new(StringComparer.Ordinal)
    {
        "attachment", "presentedForm", "contentAttachment", "valueAttachment", "photo",
    };

    // Of those, the ones that are the record's content (a note's document, a message's payload, an observation's or
    // answer's value): a record whose every such attachment loses its content is skipped. presentedForm counts only
    // when the report has nothing else (AttachmentTally.PresentedFormIsContent); a photo never does.
    private static readonly HashSet<string> ContentAttachmentProperties = new(StringComparer.Ordinal)
    {
        "attachment", "contentAttachment", "valueAttachment",
    };

    /// <summary>How many content attachments still carry content after shaping, and how many lost it.</summary>
    private sealed class AttachmentTally
    {
        public int Kept { get; set; }
        public int Emptied { get; set; }

        public bool PresentedFormIsContent { get; init; }

        public bool IsContent(string propertyName) =>
            ContentAttachmentProperties.Contains(propertyName) || (propertyName == "presentedForm" && PresentedFormIsContent);

        /// <summary>A note's first attachment: the writer brings its text inline from the source before the send
        /// (<see cref="EhrNoteContent"/>), or skips the note, so its link is left for that step.</summary>
        public JsonObject? NoteAttachment { get; set; }
    }

    private static JsonObject? FirstNoteAttachment(JsonNode? content) =>
        (content as JsonArray)?.OfType<JsonObject>().Select(c => c["attachment"] as JsonObject).FirstOrDefault(a => a is not null);

    /// <summary>
    /// Drops an attachment <c>url</c> the server cannot follow for this record: a relative one (<c>Binary/123</c>
    /// resolves against the TARGET's base, naming whatever record of its own has that id, possibly another patient's),
    /// one that is not http(s) (a <c>urn:</c> names a bundle entry), and an absolute one that addresses a FHIR resource
    /// (<c>.../Binary/123</c>: a link into the source server, which the target cannot authorise). Embedded
    /// <c>data</c> stays. A plain web document link stays.
    /// </summary>
    private static void DetachAttachmentUrl(JsonObject attachment, AttachmentTally tally, string propertyName)
    {
        var url = String(attachment, "url");
        var hasData = !string.IsNullOrWhiteSpace(String(attachment, "data"));
        if (ReferenceEquals(attachment, tally.NoteAttachment) && !hasData && url is not null)
        {
            tally.Kept++;
            return;
        }

        var removed = false;
        if (url is not null && !IsTransferableUrl(url))
        {
            attachment.Remove("url");
            removed = true;
        }

        if (!tally.IsContent(propertyName))
        {
            return;
        }

        if (hasData || attachment.ContainsKey("url"))
        {
            tally.Kept++;
        }
        else if (removed)
        {
            tally.Emptied++;
        }
    }

    private static bool IsTransferableUrl(string url) =>
        Uri.TryCreate(url.Trim(), UriKind.Absolute, out var absolute)
        && absolute.Scheme is "http" or "https"
        && !EhrFhirReference.TryParse(url, out _, out _);

    private static void DetachReferences(JsonNode? node, bool isContextOfDocument, AttachmentTally attachments, string? propertyName)
    {
        switch (node)
        {
            case JsonObject obj:
                if (isContextOfDocument)
                {
                    obj.Remove("encounter");
                }

                if (propertyName is not null && AttachmentProperties.Contains(propertyName))
                {
                    DetachAttachmentUrl(obj, attachments, propertyName);
                }

                if (obj["reference"] is JsonValue reference && reference.TryGetValue<string>(out var text) && !text.StartsWith('#'))
                {
                    var type = EhrFhirReference.TryParse(text, out var parsedType, out _) ? parsedType : "record";
                    obj.Remove("reference");
                    if (!obj.ContainsKey("display") && !obj.ContainsKey("identifier"))
                    {
                        obj["display"] = $"{type} reference not transferred";
                    }
                }

                foreach (var (childName, child) in obj.ToList())
                {
                    DetachReferences(child, isContextOfDocument: false, attachments, childName);
                }

                break;
            case JsonArray array:
                // An array's items are of its property's type (presentedForm[] are attachments).
                foreach (var child in array)
                {
                    DetachReferences(child, isContextOfDocument: false, attachments, propertyName);
                }

                break;
        }
    }
}

using System.Text.Json.Nodes;
using FHIRBridge.Application.Abstractions.Destinations;
using FHIRBridge.Domain.Enums;
using static FHIRBridge.Infrastructure.Destinations.EhrWriteBack.EhrFhirJson;

namespace FHIRBridge.Infrastructure.Destinations.EhrWriteBack.Epic;

/// <summary>
/// Epic Condition.Create (Problems), spec 949: a problem-list item into the holding tank. Epic matches the code to a
/// diagnosis record (ICD-10 and SNOMED preferred), needs <c>code.coding.display</c> or <c>code.text</c>, accepts
/// <c>verificationStatus</c> provisional only (anything else is 422 59012) and <c>clinicalStatus</c> active or resolved,
/// and keeps one note of up to 450 characters. It does not deduplicate.
/// </summary>
public sealed class EpicConditionWriteProfile : IEhrWriteProfile
{
    private const string ClinicalSystem = "http://terminology.hl7.org/CodeSystem/condition-clinical";
    private const string VerificationSystem = "http://terminology.hl7.org/CodeSystem/condition-ver-status";
    private const string CategorySystem = "http://terminology.hl7.org/CodeSystem/condition-category";
    private const int MaxNoteLength = 450;

    public SourceSystemType Vendor => SourceSystemType.Epic;

    public string ResourceType => "Condition";

    public EhrShapeResult Shape(JsonObject source, EhrWriteBackRunOptions options)
    {
        var verification = FirstCode(source, "verificationStatus");
        if (verification is "entered-in-error" or "refuted")
        {
            return EhrShapeResult.Skip(verification);
        }

        // An encounter diagnosis or health concern is not a problem-list item; filing it as one would change the
        // patient's problem list. A Condition with no category at all is treated as a problem.
        var categories = Objects(source, "category").ToList();
        if (categories.Count > 0 && !categories.Any(c => Codes(c).Contains("problem-list-item")))
        {
            return EhrShapeResult.Skip("not-a-problem-list-item");
        }

        var clinical = FirstCode(source, "clinicalStatus");
        if (clinical is "inactive")
        {
            // Epic does not support creating inactive problems.
            return EhrShapeResult.Skip("not-active");
        }

        var subject = Reference(source, "subject");
        if (subject is null)
        {
            return EhrShapeResult.Reject("missing-patient");
        }

        var code = CopyCodedConcept(Object(source, "code"));
        if (code?["coding"] is not JsonArray { Count: > 0 } codings)
        {
            return EhrShapeResult.Skip("text-only-problem");
        }

        if (String(code, "text") is null)
        {
            var display = codings.OfType<JsonObject>().Select(c => String(c, "display")).FirstOrDefault(d => d is not null);
            if (display is null)
            {
                return EhrShapeResult.Reject("missing-problem-name");
            }

            code["text"] = display;
        }

        var abatement = String(source, "abatementDateTime") ?? String(Object(source, "abatementPeriod"), "end");
        var resolved = clinical is "resolved" or "remission" || abatement is not null;

        var shaped = new JsonObject
        {
            ["resourceType"] = ResourceType,
            ["clinicalStatus"] = resolved
                ? CodeableConcept(ClinicalSystem, "resolved", "Resolved")
                : CodeableConcept(ClinicalSystem, "active", "Active"),
            ["verificationStatus"] = CodeableConcept(VerificationSystem, "provisional", "Provisional"),
            ["category"] = new JsonArray(CodeableConcept(CategorySystem, "problem-list-item", "Problem List Item")),
            ["code"] = code,
            ["subject"] = new JsonObject { ["reference"] = subject },
        };

        if (String(source, "onsetDateTime") is { } onset)
        {
            shaped["onsetDateTime"] = onset;
        }
        else if (String(Object(source, "onsetPeriod"), "start") is { } onsetStart)
        {
            shaped["onsetDateTime"] = onsetStart;
        }

        if (abatement is not null)
        {
            shaped["abatementDateTime"] = abatement;
        }

        if (NoteText(source, MaxNoteLength) is { } note)
        {
            shaped["note"] = SingleNote(note);
        }

        return EhrShapeResult.Shaped(shaped, subject);
    }

    public void BindReferences(JsonObject shaped, string targetPatientId, string? targetEncounterId) =>
        shaped["subject"] = ReferenceTo("Patient", targetPatientId);
}

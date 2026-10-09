using System.Text.Json.Nodes;
using FHIRBridge.Application.Abstractions.Destinations;
using FHIRBridge.Domain.Enums;
using static FHIRBridge.Infrastructure.Destinations.EhrWriteBack.EhrFhirJson;

namespace FHIRBridge.Infrastructure.Destinations.EhrWriteBack.Epic;

/// <summary>
/// Epic AllergyIntolerance.Create (Patient Chart), spec 945. Files into the holding tank until a clinician reconciles
/// it. Epic accepts <c>clinicalStatus</c> active and <c>verificationStatus</c> unconfirmed only (an unaccepted modifier
/// value is fatal since November 2022), takes one manifestation per reaction, and does not deduplicate.
/// </summary>
public sealed class EpicAllergyIntoleranceWriteProfile : IEhrWriteProfile
{
    private const string ClinicalSystem = "http://terminology.hl7.org/CodeSystem/allergyintolerance-clinical";
    private const string VerificationSystem = "http://terminology.hl7.org/CodeSystem/allergyintolerance-verification";

    // SNOMED "no known ..." findings: an assertion that there is no allergy, not an allergy to file.
    private static readonly HashSet<string> NoKnownAllergyCodes =
        new(StringComparer.Ordinal) { "716186003", "409137002", "429625007", "428607008", "428197003" };

    private static readonly HashSet<string> Criticalities = new(StringComparer.Ordinal) { "unable-to-assess", "high", "low" };
    private static readonly HashSet<string> Types = new(StringComparer.Ordinal) { "allergy", "intolerance" };
    private static readonly HashSet<string> Severities = new(StringComparer.Ordinal) { "mild", "moderate", "severe" };
    private static readonly HashSet<string> Categories = new(StringComparer.Ordinal) { "food", "medication", "environment", "biologic" };

    public SourceSystemType Vendor => SourceSystemType.Epic;

    public string ResourceType => "AllergyIntolerance";

    public EhrShapeResult Shape(JsonObject source, EhrWriteBackRunOptions options)
    {
        var verification = FirstCode(source, "verificationStatus");
        if (verification is "entered-in-error" or "refuted")
        {
            return EhrShapeResult.Skip(verification);
        }

        if (FirstCode(source, "clinicalStatus") is "inactive" or "resolved")
        {
            return EhrShapeResult.Skip("not-active");
        }

        var codeSource = Object(source, "code");
        if (Codes(codeSource).Any(NoKnownAllergyCodes.Contains))
        {
            return EhrShapeResult.Skip("no-known-allergies");
        }

        var patient = Reference(source, "patient");
        if (patient is null)
        {
            return EhrShapeResult.Reject("missing-patient");
        }

        var code = CopyCodedConcept(codeSource);
        if (code is null)
        {
            return EhrShapeResult.Reject("missing-code");
        }

        var shaped = new JsonObject
        {
            ["resourceType"] = ResourceType,
            ["clinicalStatus"] = CodeableConcept(ClinicalSystem, "active", "Active"),
            ["verificationStatus"] = CodeableConcept(VerificationSystem, "unconfirmed", "Unconfirmed"),
        };

        var categories = new JsonArray();
        foreach (var category in Array(source, "category")?.OfType<JsonValue>() ?? [])
        {
            if (category.TryGetValue<string>(out var value) && Categories.Contains(value))
            {
                categories.Add(value);
            }
        }

        if (categories.Count > 0)
        {
            shaped["category"] = categories;
        }

        if (String(source, "criticality") is { } criticality && Criticalities.Contains(criticality))
        {
            shaped["criticality"] = criticality;
        }

        if (String(source, "type") is { } type && Types.Contains(type))
        {
            shaped["type"] = type;
        }

        shaped["code"] = code;
        shaped["patient"] = new JsonObject { ["reference"] = patient };
        CopyIfString(source, shaped, "onsetDateTime");
        if (!shaped.ContainsKey("onsetDateTime"))
        {
            CopyIfPresent(source, shaped, "onsetPeriod");
        }

        CopyIfString(source, shaped, "recordedDate");
        if (NoteText(source, 2000) is { } note)
        {
            shaped["note"] = SingleNote(note);
        }

        var reactions = ShapeReactions(source);
        if (reactions.Count > 0)
        {
            shaped["reaction"] = reactions;
        }

        return EhrShapeResult.Shaped(shaped, patient);
    }

    public void BindReferences(JsonObject shaped, string targetPatientId, string? targetEncounterId) =>
        shaped["patient"] = ReferenceTo("Patient", targetPatientId);

    /// <summary>Epic takes exactly one manifestation per reaction, so the first is kept.</summary>
    private static JsonArray ShapeReactions(JsonObject source)
    {
        var reactions = new JsonArray();
        foreach (var reaction in Objects(source, "reaction"))
        {
            var manifestation = Objects(reaction, "manifestation")
                .Select(m => CopyCodedConcept(m))
                .FirstOrDefault(m => m is not null);
            if (manifestation is null)
            {
                continue;
            }

            var shaped = new JsonObject { ["manifestation"] = new JsonArray(manifestation) };
            CopyIfString(reaction, shaped, "description");
            if (String(reaction, "severity") is { } severity && Severities.Contains(severity))
            {
                shaped["severity"] = severity;
            }

            reactions.Add(shaped);
        }

        return reactions;
    }
}

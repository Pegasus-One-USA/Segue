using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using FHIRBridge.Application.Abstractions.Destinations;
using FHIRBridge.Domain.Enums;
using FHIRBridge.Domain.Fhir;
using FHIRBridge.Infrastructure.Destinations.EhrWriteBack.Epic;
using static FHIRBridge.Infrastructure.Destinations.EhrWriteBack.EhrFhirJson;

namespace FHIRBridge.Infrastructure.Destinations.EhrWriteBack.Healow;

// eClinicalWorks (Healow) write profiles, from eCW's published Create API documentation
// (fhir.eclinicalworks.com/ecwopendev/documentation/create-resources, published 2026-08-07). Each profile builds only
// the elements the API's schema table lists: eCW ignores anything else. The eCW channel wraps the result in the
// transaction Bundle eCW takes; see EcwEhrWriteChannel and docs/backend/20-epic-r4-write-back.md section 13. Most of
// these land in eCW's "App Data" tab, where a user reconciles them into the chart.

/// <summary>eCW AllergyIntolerance create (contracted, 12.0.2+). Needs <c>code</c> (display preferred, then text) and
/// <c>patient</c>; takes clinicalStatus active or inactive, criticality, onset, and reactions as
/// <c>manifestation.text</c> "{type} : {reaction}". No encounter.</summary>
public sealed class HealowAllergyIntoleranceWriteProfile : IEhrWriteProfile
{
    private const string ClinicalSystem = "http://terminology.hl7.org/CodeSystem/allergyintolerance-clinical";

    private static readonly HashSet<string> NoKnownAllergyCodes =
        new(StringComparer.Ordinal) { "716186003", "409137002", "429625007", "428607008", "428197003" };

    private static readonly HashSet<string> Criticalities = new(StringComparer.Ordinal) { "unable-to-assess", "high", "low" };

    public SourceSystemType Vendor => SourceSystemType.Healow;

    public string ResourceType => "AllergyIntolerance";

    public EhrShapeResult Shape(JsonObject source, EhrWriteBackRunOptions options)
    {
        var verification = FirstCode(source, "verificationStatus");
        if (verification is "entered-in-error" or "refuted")
        {
            return EhrShapeResult.Skip(verification);
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
        if (code is null || DisplayName(code) is null)
        {
            return EhrShapeResult.Reject("missing-allergen-name");
        }

        // eCW matches the allergen by its display, then its text: make sure the display is there.
        if (code["coding"] is JsonArray codings)
        {
            foreach (var coding in codings.OfType<JsonObject>().Where(c => String(c, "display") is null))
            {
                coding["display"] = DisplayName(code);
            }
        }

        var clinical = FirstCode(source, "clinicalStatus") is "inactive" or "resolved" ? "inactive" : "active";
        var shaped = new JsonObject
        {
            ["resourceType"] = ResourceType,
            ["clinicalStatus"] = CodeableConcept(ClinicalSystem, clinical),
            ["code"] = code,
            ["patient"] = new JsonObject { ["reference"] = patient },
        };

        if (String(source, "onsetDateTime") is { } onset)
        {
            shaped["onsetDateTime"] = onset;
        }

        if (String(source, "criticality") is { } criticality && Criticalities.Contains(criticality))
        {
            shaped["criticality"] = criticality;
        }

        var reactions = new JsonArray();
        foreach (var reaction in Objects(source, "reaction"))
        {
            foreach (var manifestation in Objects(reaction, "manifestation"))
            {
                if (DisplayName(manifestation) is { } name)
                {
                    reactions.Add(new JsonObject
                    {
                        ["manifestation"] = new JsonArray(new JsonObject { ["text"] = $"Allergy : {name}" }),
                    });
                }
            }
        }

        if (reactions.Count > 0)
        {
            shaped["reaction"] = reactions;
        }

        return EhrShapeResult.Shaped(shaped, patient);
    }

    public void BindReferences(JsonObject shaped, string targetPatientId, string? targetEncounterId) =>
        shaped["patient"] = ReferenceTo("Patient", targetPatientId);
}

/// <summary>Shared rules of eCW's coded Condition APIs (problems and encounter diagnoses): <c>code</c> with ICD-10
/// preferred, then SNOMED, in that order; nothing else is accepted.</summary>
public abstract class HealowCodedConditionWriteProfile : IEhrWriteProfile
{
    protected const string ClinicalSystem = "http://terminology.hl7.org/CodeSystem/condition-clinical";
    protected const string CategorySystem = "http://terminology.hl7.org/CodeSystem/condition-category";

    public SourceSystemType Vendor => SourceSystemType.Healow;

    public string ResourceType => "Condition";

    public abstract string? Variant { get; }

    public abstract EhrShapeResult Shape(JsonObject source, EhrWriteBackRunOptions options);

    public virtual void BindReferences(JsonObject shaped, string targetPatientId, string? targetEncounterId) =>
        shaped["subject"] = ReferenceTo("Patient", targetPatientId);

    /// <summary>The ICD-10-CM codings first, then SNOMED, with the condition's text; null when it has neither.</summary>
    protected static JsonObject? IcdThenSnomed(JsonObject source)
    {
        var codeSource = Object(source, "code");
        var codings = new JsonArray();
        foreach (var system in new[] { Icd10CmSystem, SnomedSystem })
        {
            foreach (var coding in Objects(codeSource, "coding").Where(c => string.Equals(String(c, "system"), system, StringComparison.OrdinalIgnoreCase)))
            {
                if (String(coding, "code") is not { Length: > 0 } code)
                {
                    continue;
                }

                var copy = new JsonObject { ["system"] = system, ["code"] = code };
                CopyIfString(coding, copy, "display");
                codings.Add(copy);
            }
        }

        if (codings.Count == 0)
        {
            return null;
        }

        var concept = new JsonObject { ["coding"] = codings };
        if (DisplayName(codeSource) is { } text)
        {
            concept["text"] = text;
        }

        return concept;
    }

    protected static string? Onset(JsonObject source) =>
        String(source, "onsetDateTime") ?? String(Object(source, "onsetPeriod"), "start");

    protected static bool HasCategory(JsonObject source, string code) =>
        Objects(source, "category").Any(c => Codes(c).Contains(code));
}

/// <summary>eCW Condition (Problems), contracted, 12.0.2+: App Data, then the Problem List. Takes clinicalStatus active,
/// resolved, inactive, recurrence or remission (resolved needs <c>abatementDateTime</c>), verificationStatus, onset and
/// a note.</summary>
public sealed class HealowConditionWriteProfile : HealowCodedConditionWriteProfile
{
    private const string VerificationSystem = "http://terminology.hl7.org/CodeSystem/condition-ver-status";
    private static readonly HashSet<string> ClinicalStatuses = new(StringComparer.Ordinal) { "active", "resolved", "inactive", "recurrence", "remission" };
    private static readonly HashSet<string> VerificationStatuses = new(StringComparer.Ordinal) { "unconfirmed", "provisional", "differential", "confirmed" };

    public override string? Variant => EhrWriteVariants.ProblemListItem;

    public override EhrShapeResult Shape(JsonObject source, EhrWriteBackRunOptions options)
    {
        var verification = FirstCode(source, "verificationStatus");
        if (verification is "entered-in-error" or "refuted")
        {
            return EhrShapeResult.Skip(verification);
        }

        // A Condition with no category at all is treated as a problem, as Epic's profile does.
        if (Objects(source, "category").Any() && !HasCategory(source, "problem-list-item"))
        {
            return EhrShapeResult.Skip("not-a-problem-list-item");
        }

        var subject = Reference(source, "subject");
        if (subject is null)
        {
            return EhrShapeResult.Reject("missing-patient");
        }

        var code = IcdThenSnomed(source);
        if (code is null)
        {
            return EhrShapeResult.Reject("missing-icd10-or-snomed-code");
        }

        var clinical = FirstCode(source, "clinicalStatus");
        var abatement = String(source, "abatementDateTime") ?? String(Object(source, "abatementPeriod"), "end");
        if (clinical is null)
        {
            clinical = abatement is null ? "active" : "resolved";
        }

        if (!ClinicalStatuses.Contains(clinical))
        {
            return EhrShapeResult.Reject("unsupported-clinical-status");
        }

        if (clinical == "resolved" && abatement is null)
        {
            return EhrShapeResult.Reject("resolved-without-abatement-date");
        }

        var shaped = new JsonObject
        {
            ["resourceType"] = ResourceType,
            ["clinicalStatus"] = CodeableConcept(ClinicalSystem, clinical),
        };
        if (verification is not null && VerificationStatuses.Contains(verification))
        {
            shaped["verificationStatus"] = CodeableConcept(VerificationSystem, verification);
        }

        shaped["category"] = new JsonArray(CodeableConcept(CategorySystem, "problem-list-item", "Problem List Item"));
        shaped["code"] = code;
        shaped["subject"] = new JsonObject { ["reference"] = subject };
        if (Onset(source) is { } onset)
        {
            shaped["onsetDateTime"] = onset;
        }

        if (abatement is not null)
        {
            shaped["abatementDateTime"] = abatement;
        }

        if (NoteText(source, 1000) is { } note)
        {
            shaped["note"] = SingleNote(note);
        }

        return EhrShapeResult.Shaped(shaped, subject);
    }
}

/// <summary>eCW Condition (Encounter Diagnosis), contracted, 12.0.2+: App Data, then the Assessments section. The API
/// documents no encounter element, so the record carries none; which visit eCW files it under is eCW's.</summary>
public sealed class HealowEncounterDiagnosisWriteProfile : HealowCodedConditionWriteProfile
{
    public override string? Variant => EhrWriteVariants.EncounterDiagnosis;

    public override EhrShapeResult Shape(JsonObject source, EhrWriteBackRunOptions options)
    {
        if (FirstCode(source, "verificationStatus") is "entered-in-error" or "refuted")
        {
            return EhrShapeResult.Skip(FirstCode(source, "verificationStatus")!);
        }

        if (!HasCategory(source, "encounter-diagnosis"))
        {
            return EhrShapeResult.Skip("not-an-encounter-diagnosis");
        }

        var subject = Reference(source, "subject");
        if (subject is null)
        {
            return EhrShapeResult.Reject("missing-patient");
        }

        var code = IcdThenSnomed(source);
        if (code is null)
        {
            return EhrShapeResult.Reject("missing-icd10-or-snomed-code");
        }

        var shaped = new JsonObject
        {
            ["resourceType"] = ResourceType,
            ["category"] = new JsonArray(CodeableConcept(CategorySystem, "encounter-diagnosis", "Encounter Diagnosis")),
            ["code"] = code,
            ["subject"] = new JsonObject { ["reference"] = subject },
        };
        if (Onset(source) is { } onset)
        {
            shaped["onsetDateTime"] = onset;
        }

        return EhrShapeResult.Shaped(shaped, subject);
    }
}

/// <summary>eCW Condition (Medical History), contracted: category SNOMED 435871000124102, <c>code.text</c> only (eCW
/// reads no coding), on an open telephone encounter the writer creates (opt-in). A source condition is medical
/// history when it is categorised so: <c>medical-history</c> (Epic) or that SNOMED code.</summary>
public sealed class HealowMedicalHistoryWriteProfile : HealowCodedConditionWriteProfile
{
    private const string MedicalHistoryCode = "435871000124102";

    public override string? Variant => EhrWriteVariants.MedicalHistory;

    public override EhrShapeResult Shape(JsonObject source, EhrWriteBackRunOptions options)
    {
        if (FirstCode(source, "verificationStatus") is "entered-in-error" or "refuted")
        {
            return EhrShapeResult.Skip(FirstCode(source, "verificationStatus")!);
        }

        if (!HasCategory(source, "medical-history") && !HasCategory(source, MedicalHistoryCode))
        {
            return EhrShapeResult.Skip("not-a-medical-history-item");
        }

        var subject = Reference(source, "subject");
        if (subject is null)
        {
            return EhrShapeResult.Reject("missing-patient");
        }

        if (DisplayName(Object(source, "code")) is not { } text)
        {
            return EhrShapeResult.Reject("missing-condition-name");
        }

        var shaped = new JsonObject
        {
            ["resourceType"] = ResourceType,
            ["category"] = new JsonArray(CodeableConcept(SnomedSystem, MedicalHistoryCode, "Medical history")),
            ["code"] = new JsonObject { ["text"] = text },
            ["subject"] = new JsonObject { ["reference"] = subject },
        };
        if (Onset(source) is { } onset)
        {
            shaped["onsetDateTime"] = onset;
        }

        return EhrShapeResult.Shaped(shaped, subject);
    }

    public override void BindReferences(JsonObject shaped, string targetPatientId, string? targetEncounterId) =>
        BindWithEncounter(shaped, targetPatientId, targetEncounterId);

    internal static void BindWithEncounter(JsonObject shaped, string targetPatientId, string? targetEncounterId)
    {
        shaped["subject"] = ReferenceTo("Patient", targetPatientId);
        if (targetEncounterId is not null)
        {
            shaped["encounter"] = ReferenceTo("Encounter", targetEncounterId);
        }
    }
}

/// <summary>eCW Procedure (Surgical History), contracted: status completed, category SNOMED 387713003,
/// <c>code.text</c>, an optional <c>performedDateTime</c> as YYYY-MM, on an open telephone encounter the writer creates
/// (opt-in). Only completed surgical procedures (that category) are sent: a blood draw is not surgical history.</summary>
public sealed class HealowSurgicalHistoryWriteProfile : IEhrWriteProfile
{
    private const string SurgicalProcedureCode = "387713003";

    public SourceSystemType Vendor => SourceSystemType.Healow;

    public string ResourceType => "Procedure";

    public string? Variant => EhrWriteVariants.SurgicalHistory;

    public EhrShapeResult Shape(JsonObject source, EhrWriteBackRunOptions options)
    {
        var status = String(source, "status");
        if (status != "completed")
        {
            return EhrShapeResult.Skip(status is "entered-in-error" ? status : "not-completed");
        }

        if (!Codes(Object(source, "category")).Contains(SurgicalProcedureCode))
        {
            return EhrShapeResult.Skip("not-a-surgical-procedure");
        }

        var subject = Reference(source, "subject");
        if (subject is null)
        {
            return EhrShapeResult.Reject("missing-patient");
        }

        if (DisplayName(Object(source, "code")) is not { } text)
        {
            return EhrShapeResult.Reject("missing-procedure-name");
        }

        var shaped = new JsonObject
        {
            ["resourceType"] = ResourceType,
            ["status"] = "completed",
            ["category"] = CodeableConcept(SnomedSystem, SurgicalProcedureCode, "Surgical procedure"),
            ["code"] = new JsonObject { ["text"] = text },
            ["subject"] = new JsonObject { ["reference"] = subject },
        };

        var performed = DateParts(String(source, "performedDateTime") ?? String(Object(source, "performedPeriod"), "start"));
        if (performed is { Month: { } month } parts)
        {
            shaped["performedDateTime"] = $"{parts.Year:D4}-{month:D2}";
        }

        return EhrShapeResult.Shaped(shaped, subject);
    }

    public void BindReferences(JsonObject shaped, string targetPatientId, string? targetEncounterId) =>
        HealowMedicalHistoryWriteProfile.BindWithEncounter(shaped, targetPatientId, targetEncounterId);
}

/// <summary>eCW Immunization (Create), contracted, 12.0.2+: historical only (<c>primarySource</c> false). Needs status
/// completed, a CVX vaccine code and an occurrence date.</summary>
public sealed class HealowImmunizationWriteProfile : IEhrWriteProfile
{
    public SourceSystemType Vendor => SourceSystemType.Healow;

    public string ResourceType => "Immunization";

    public EhrShapeResult Shape(JsonObject source, EhrWriteBackRunOptions options)
    {
        var status = String(source, "status");
        if (status != "completed")
        {
            return EhrShapeResult.Skip(status is "entered-in-error" ? status : "not-completed");
        }

        var patient = Reference(source, "patient");
        if (patient is null)
        {
            return EhrShapeResult.Reject("missing-patient");
        }

        var vaccine = Object(source, "vaccineCode");
        if (CodeOf(vaccine, CvxSystem) is not { } cvx)
        {
            return EhrShapeResult.Reject("missing-cvx-code");
        }

        if (String(source, "occurrenceDateTime") is not { } occurred)
        {
            return EhrShapeResult.Reject("missing-occurrence-date");
        }

        var shaped = new JsonObject
        {
            ["resourceType"] = ResourceType,
            ["status"] = "completed",
            ["vaccineCode"] = CodeableConcept(CvxSystem, cvx, DisplayName(vaccine)),
            ["patient"] = new JsonObject { ["reference"] = patient },
            ["occurrenceDateTime"] = occurred,
            ["primarySource"] = false,
        };
        CopyIfPresent(source, shaped, "reportOrigin");
        CopyIfPresent(source, shaped, "site");
        CopyIfPresent(source, shaped, "route");
        CopyIfPresent(source, shaped, "doseQuantity");

        var doses = new JsonArray();
        foreach (var protocol in Objects(source, "protocolApplied").Take(20))
        {
            if ((protocol["doseNumberPositiveInt"] as JsonValue)?.TryGetValue<int>(out var dose) == true)
            {
                doses.Add(new JsonObject { ["doseNumberPositiveInt"] = dose });
            }
        }

        if (doses.Count > 0)
        {
            shaped["protocolApplied"] = doses;
        }

        return EhrShapeResult.Shaped(shaped, patient);
    }

    public void BindReferences(JsonObject shaped, string targetPatientId, string? targetEncounterId) =>
        shaped["patient"] = ReferenceTo("Patient", targetPatientId);
}

/// <summary>
/// eCW MedicationStatement (Reconciliation), contracted, 12.0.2+: App Data, then the Medication List. Fed from a
/// source MedicationStatement or MedicationRequest (one subclass each). Needs an RxNorm or NDC code AND the
/// medication's text; takes the effective period and one dosage (text, timing, route, dose, as-needed).
/// </summary>
public abstract class HealowMedicationListWriteProfile : IEhrWriteProfile
{
    private static readonly HashSet<string> NotOnTheList = new(StringComparer.Ordinal) { "entered-in-error", "cancelled", "draft", "not-taken", "unknown" };

    public SourceSystemType Vendor => SourceSystemType.Healow;

    public abstract string ResourceType { get; }

    public EhrShapeResult Shape(JsonObject source, EhrWriteBackRunOptions options)
    {
        var status = String(source, "status");
        if (status is not null && NotOnTheList.Contains(status))
        {
            return EhrShapeResult.Skip(status);
        }

        if (ResourceType == "MedicationRequest" && String(source, "intent") is "proposal" or "plan")
        {
            return EhrShapeResult.Skip("not-an-order");
        }

        var subject = Reference(source, "subject");
        if (subject is null)
        {
            return EhrShapeResult.Reject("missing-patient");
        }

        var concept = MedicationConcept(source);
        if (concept is null)
        {
            return EhrShapeResult.Reject("medication-not-inline");
        }

        var rxnorm = CodeOf(concept, RxNormSystem);
        var ndc = CodeOf(concept, NdcSystem);
        if (rxnorm is null && ndc is null)
        {
            return EhrShapeResult.Reject("missing-rxnorm-or-ndc-code");
        }

        if (DisplayName(concept) is not { } name)
        {
            return EhrShapeResult.Reject("missing-medication-name");
        }

        var codings = new JsonArray();
        if (rxnorm is not null)
        {
            codings.Add(new JsonObject { ["system"] = RxNormSystem, ["code"] = rxnorm });
        }

        if (ndc is not null)
        {
            codings.Add(new JsonObject { ["system"] = NdcSystem, ["code"] = ndc });
        }

        var shaped = new JsonObject
        {
            ["resourceType"] = "MedicationStatement",
            // Required by eCW's profile but not read.
            ["status"] = status is "completed" or "stopped" or "on-hold" or "intended" ? status : "active",
            ["medicationCodeableConcept"] = new JsonObject { ["coding"] = codings, ["text"] = name },
            ["subject"] = new JsonObject { ["reference"] = subject },
        };

        var period = Object(source, "effectivePeriod")?.DeepClone()
            ?? (Object(Object(source, "dispenseRequest"), "validityPeriod")?.DeepClone());
        if (period is null && (String(source, "effectiveDateTime") ?? String(source, "authoredOn")) is { } start)
        {
            period = new JsonObject { ["start"] = start };
        }

        if (period is JsonObject effective)
        {
            shaped["effectivePeriod"] = effective;
        }

        var dosageSource = Objects(source, "dosage").FirstOrDefault() ?? Objects(source, "dosageInstruction").FirstOrDefault();
        if (dosageSource is not null)
        {
            var dosage = new JsonObject();
            CopyIfString(dosageSource, dosage, "text");
            if (DisplayName(Object(Object(dosageSource, "timing"), "code")) is { } timing)
            {
                dosage["timing"] = new JsonObject { ["code"] = new JsonObject { ["text"] = timing } };
            }

            if (DisplayName(Object(dosageSource, "route")) is { } route)
            {
                dosage["route"] = new JsonObject { ["text"] = route };
            }

            if (Objects(dosageSource, "doseAndRate").Select(d => Object(d, "doseQuantity")).FirstOrDefault(q => q is not null) is { } dose)
            {
                dosage["doseAndRate"] = new JsonArray(new JsonObject { ["doseQuantity"] = dose.DeepClone() });
            }

            CopyIfPresent(dosageSource, dosage, "asNeededBoolean");
            if (dosage.Count > 0)
            {
                shaped["dosage"] = new JsonArray(dosage);
            }
        }

        return EhrShapeResult.Shaped(shaped, subject);
    }

    public void BindReferences(JsonObject shaped, string targetPatientId, string? targetEncounterId) =>
        shaped["subject"] = ReferenceTo("Patient", targetPatientId);

    /// <summary>The medication as a CodeableConcept: <c>medicationCodeableConcept</c>, or a contained Medication's
    /// code. A reference to a Medication elsewhere is not followed.</summary>
    internal static JsonObject? MedicationConcept(JsonObject source)
    {
        if (Object(source, "medicationCodeableConcept") is { } concept)
        {
            return concept;
        }

        var reference = Reference(source, "medicationReference");
        if (reference is not { Length: > 1 } || reference[0] != '#')
        {
            return null;
        }

        return Objects(source, "contained")
            .Where(c => String(c, "resourceType") == "Medication" && String(c, "id") == reference[1..])
            .Select(c => Object(c, "code"))
            .FirstOrDefault(c => c is not null);
    }
}

public sealed class HealowMedicationStatementWriteProfile : HealowMedicationListWriteProfile
{
    public override string ResourceType => "MedicationStatement";
}

public sealed class HealowMedicationRequestWriteProfile : HealowMedicationListWriteProfile
{
    public override string ResourceType => "MedicationRequest";
}

/// <summary>
/// eCW Observation (Vital Signs), contracted, 12.0.3.04009405+. The Epic vital-signs subset, restricted to the LOINC
/// codes eCW accepts and with UCUM units on blood-pressure components. The record carries no encounter: eCW files it
/// on the appointment whose date and time match <c>effectiveDateTime</c>, else under Vitals Notes.
/// </summary>
public sealed class HealowVitalSignWriteProfile : IEhrWriteProfile
{
    private const string CategorySystem = "http://terminology.hl7.org/CodeSystem/observation-category";

    private static readonly HashSet<string> AcceptedLoincCodes = new(StringComparer.Ordinal)
    {
        "9279-1", "8867-4", "8310-5", "8302-2", "9843-4", "29463-7", "39156-5", "59576-9", "8289-1", "77606-2", "85354-9",
        "2708-6", "59408-5",
    };

    private readonly EpicVitalSignWriteProfile _epic = new();

    public SourceSystemType Vendor => SourceSystemType.Healow;

    public string ResourceType => "Observation";

    public string? Variant => EhrWriteVariants.VitalSigns;

    public EhrShapeResult Shape(JsonObject source, EhrWriteBackRunOptions options)
    {
        var shaped = _epic.Shape(source, options);
        if (shaped.Outcome != EhrShapeOutcome.Shaped || shaped.Resource is not { } resource)
        {
            return shaped;
        }

        var code = CodeOf(Object(resource, "code"), LoincSystem);
        if (code is null || !AcceptedLoincCodes.Contains(code))
        {
            return EhrShapeResult.Skip("vital-not-accepted-by-ecw");
        }

        resource["category"] = new JsonArray(CodeableConcept(CategorySystem, "vital-signs", "Vital Signs"));
        foreach (var component in Objects(resource, "component"))
        {
            if (Object(component, "valueQuantity") is { } quantity)
            {
                quantity["unit"] = "mm[Hg]";
                quantity["system"] = UcumSystem;
                quantity["code"] = "mm[Hg]";
            }
        }

        return EhrShapeResult.Shaped(resource, shaped.SourcePatientReference);
    }

    public void BindReferences(JsonObject shaped, string targetPatientId, string? targetEncounterId) =>
        shaped["subject"] = ReferenceTo("Patient", targetPatientId);
}

/// <summary>eCW Patient (Create), contracted, 12.0.2+. Created directly unless eCW finds the account number and birth
/// date (then 202, already at target). Needs at least one identifier, a usual name, gender (male, female or unknown)
/// and birth date; the Epic demographics subset otherwise.</summary>
public sealed class HealowPatientWriteProfile : IEhrWriteProfile
{
    private readonly EpicPatientWriteProfile _epic = new();

    public SourceSystemType Vendor => SourceSystemType.Healow;

    public string ResourceType => "Patient";

    public EhrShapeResult Shape(JsonObject source, EhrWriteBackRunOptions options)
    {
        var shaped = _epic.Shape(source, options);
        if (shaped.Outcome != EhrShapeOutcome.Shaped || shaped.Resource is not { } resource)
        {
            return shaped;
        }

        if (Array(resource, "identifier") is not { Count: > 0 })
        {
            return EhrShapeResult.Reject("missing-identifier");
        }

        resource.Remove("active");
        foreach (var name in Objects(resource, "name"))
        {
            name["use"] = "usual";
        }

        if (String(resource, "gender") == "other")
        {
            resource["gender"] = "unknown";
        }

        return EhrShapeResult.Shaped(resource, shaped.SourcePatientReference);
    }

    public void BindReferences(JsonObject shaped, string targetPatientId, string? targetEncounterId)
    {
        // A patient references nothing the writer needs to rebind.
    }
}

/// <summary>
/// eCW DocumentReference (Clinical Notes), contracted, 12.0.2+: straight into the encounter's Progress Note. The note
/// text travels as an HL7 v2 message in the attachment (<c>text/hl7v2</c>). eCW's own sample is an ORU^R01 from
/// sending application PROG with one OBR per progress-note section and the text in OBX-5; this profile sends one
/// section. Needs an author (the destination's provider id) and an existing encounter of the patient, whose id is in
/// both <c>context.encounter</c> and the OBR.
/// <para>UNCONFIRMED until a contracted practice is available: whether the section names below are the ones eCW
/// expects for each note type, and whether PID-3 takes the eCW FHIR patient id.</para>
/// </summary>
public sealed class HealowClinicalNoteWriteProfile : IEhrWriteProfile
{
    private const string CategorySystem = "http://hl7.org/fhir/us/core/CodeSystem/us-core-documentreference-category";
    private const string PlainTextProperty = "_plainText";

    // Patient instructions are not a progress note.
    private static readonly IReadOnlySet<string> ExcludedLoincTypes = new HashSet<string>(StringComparer.Ordinal) { "69730-0" };

    private static readonly IReadOnlyDictionary<string, string> SectionByLoinc = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["34117-2"] = "HPI",
        ["11488-4"] = "Assessment",
        ["28570-0"] = "Procedure",
    };

    private const string DefaultSection = "Treatment";

    public SourceSystemType Vendor => SourceSystemType.Healow;

    public string ResourceType => "DocumentReference";

    public string? Variant => EhrWriteVariants.ClinicalNote;

    public EhrShapeResult Shape(JsonObject source, EhrWriteBackRunOptions options)
    {
        var (note, problem) = EhrClinicalNote.Parse(source, ExcludedLoincTypes);
        if (note is null)
        {
            return problem!;
        }

        if (string.IsNullOrWhiteSpace(options.TargetProviderId))
        {
            return EhrShapeResult.Reject("note-author-not-configured");
        }

        var shaped = new JsonObject
        {
            ["resourceType"] = ResourceType,
            ["status"] = "current",
            ["type"] = note.Type,
            ["category"] = new JsonArray(CodeableConcept(CategorySystem, "clinical-note", "Clinical Note")),
            ["subject"] = new JsonObject { ["reference"] = note.Subject },
            ["author"] = new JsonArray(ReferenceTo("Practitioner", options.TargetProviderId!)),
            // Replaced by the HL7 message once the patient and encounter are known (BindReferences); never sent.
            [PlainTextProperty] = note.Text,
            ["_section"] = note.LoincCode is { } loinc && SectionByLoinc.TryGetValue(loinc, out var section) ? section : DefaultSection,
        };
        if (note.Date is { } date)
        {
            shaped["date"] = date;
        }

        return EhrShapeResult.Shaped(shaped, note.Subject, note.SourceEncounter);
    }

    public void BindReferences(JsonObject shaped, string targetPatientId, string? targetEncounterId)
    {
        shaped["subject"] = ReferenceTo("Patient", targetPatientId);
        if (targetEncounterId is null)
        {
            return;
        }

        shaped["context"] = new JsonObject { ["encounter"] = new JsonArray(ReferenceTo("Encounter", targetEncounterId)) };
        var text = String(shaped, PlainTextProperty) ?? string.Empty;
        var section = String(shaped, "_section") ?? DefaultSection;
        var message = BuildOruMessage(targetPatientId, targetEncounterId, section, text, String(shaped, "date"));
        shaped.Remove(PlainTextProperty);
        shaped.Remove("_section");
        shaped["content"] = new JsonArray(new JsonObject
        {
            ["attachment"] = new JsonObject
            {
                ["contentType"] = "text/hl7v2",
                ["data"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(message)),
            },
        });
    }

    /// <summary>
    /// The ORU^R01 eCW's sample uses: MSH from PROG, PID, then OBR (encounter id, section) and OBX TX with the text.
    /// Deterministic for the same note (time from the note's date, control id from its content), so the ledger's
    /// content hash is stable across runs.
    /// </summary>
    internal static string BuildOruMessage(string patientId, string encounterId, string section, string text, string? noteDate)
    {
        var parts = DateParts(noteDate);
        var timestamp = parts is { } p ? $"{p.Year:D4}{p.Month ?? 1:D2}{p.Day ?? 1:D2}" : "19700101";
        var controlId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{patientId}|{encounterId}|{section}|{text}")))[..20];
        var sectionField = $"{Escape(section)}^{Escape(section)}";
        return string.Join(
            "\r",
            $"MSH|^~\\&|PROG|PROG|PROG||{timestamp}||ORU^R01|{controlId}|T|2.4",
            $"PID|1||{Escape(patientId)}",
            $"OBR|1|{Escape(encounterId)}||{sectionField}",
            $"OBX|1|TX|||{Escape(text)}") + "\r";
    }

    /// <summary>HL7 v2 escapes for the default delimiters; line breaks become <c>\.br\</c>.</summary>
    internal static string Escape(string value)
    {
        var builder = new StringBuilder(value.Length + 16);
        foreach (var character in value.Replace("\r\n", "\n", StringComparison.Ordinal))
        {
            builder.Append(character switch
            {
                '\\' => "\\E\\",
                '|' => "\\F\\",
                '^' => "\\S\\",
                '&' => "\\T\\",
                '~' => "\\R\\",
                '\n' or '\r' => "\\.br\\",
                _ => character.ToString(),
            });
        }

        return builder.ToString();
    }
}

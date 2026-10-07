using System.Globalization;
using System.Text.Json.Nodes;
using FHIRBridge.Application.Abstractions.Destinations;
using FHIRBridge.Domain.Enums;
using FHIRBridge.Domain.Fhir;
using FHIRBridge.Infrastructure.Destinations.EhrWriteBack.Epic;
using static FHIRBridge.Infrastructure.Destinations.EhrWriteBack.EhrFhirJson;
using R = FHIRBridge.Application.Abstractions.Destinations.AthenaOneWriteRequest;

namespace FHIRBridge.Infrastructure.Destinations.EhrWriteBack.Athenahealth;

// athenahealth write profiles over the proprietary athenaOne REST API (/v1/{practiceid}/...), from athena's API
// reference (docs.athenahealth.com/api/api-ref). athena's FHIR API creates only QuestionnaireResponse, so each profile
// shapes a description of one athenaOne call (AthenaOneWriteRequest) that the athenaOne write channel sends. Facts
// the reference leaves open, and that the first preview run must confirm, are marked UNCONFIRMED here and in
// docs/backend/20-epic-r4-write-back.md section 13.

/// <summary>Shared shape of an athenaOne request description.</summary>
public abstract class AthenaOneWriteProfile : IEhrWriteProfile
{
    public SourceSystemType Vendor => SourceSystemType.Athenahealth;

    public abstract string ResourceType { get; }

    public virtual string? Variant => null;

    public abstract EhrShapeResult Shape(JsonObject source, EhrWriteBackRunOptions options);

    public virtual void BindReferences(JsonObject shaped, string targetPatientId, string? targetEncounterId)
    {
        shaped[R.Patient] = targetPatientId;
        if (targetEncounterId is not null)
        {
            shaped[R.Encounter] = targetEncounterId;
        }
    }

    protected static JsonObject Request(string operation, string method, string path, bool department, string? idField, bool multipart = false)
    {
        var request = new JsonObject
        {
            ["resourceType"] = R.ResourceType,
            [R.Operation] = operation,
            [R.Method] = method,
            [R.Path] = path,
            [R.Multipart] = multipart,
            [R.Department] = department,
            [R.Fields] = new JsonObject(),
        };
        if (idField is not null)
        {
            request[R.IdField] = idField;
        }

        return request;
    }

    protected static JsonObject Fields(JsonObject request) => (JsonObject)request[R.Fields]!;

    protected static void Lookup(JsonObject request, string kind, string name, string token)
    {
        var lookups = request[R.Lookups] as JsonArray ?? new JsonArray();
        lookups.Add(new JsonObject { [R.LookupKind] = kind, [R.LookupName] = name, [R.LookupToken] = token });
        request[R.Lookups] = lookups;
    }

    protected static void JsonField(JsonObject request, string name, JsonNode value)
    {
        var jsonFields = request[R.JsonFields] as JsonObject ?? new JsonObject();
        jsonFields[name] = value;
        request[R.JsonFields] = jsonFields;
    }

    /// <summary>athenaOne's date format, MM/DD/YYYY; null unless the FHIR date has a day.</summary>
    protected static string? AthenaDate(string? fhirDate) =>
        DateParts(fhirDate) is { Month: { } month, Day: { } day } parts
            ? $"{month:D2}/{day:D2}/{parts.Year:D4}"
            : null;

    /// <summary>The time part of a FHIR dateTime as HH:mm, or null.</summary>
    protected static string? AthenaTime(string? fhirDateTime) =>
        fhirDateTime is { Length: >= 16 } && fhirDateTime[10] == 'T' ? fhirDateTime.Substring(11, 5) : null;
}

/// <summary>athenaOne <c>PUT /chart/{patientid}/allergies</c>: upserts by allergenid, never deletes. The allergen is
/// looked up by exact name in athena's allergen list (<c>/reference/allergies</c>); reactions carry their name, SNOMED
/// code and severity. No id comes back. Inactive allergies are not sent.</summary>
public sealed class AthenaOneAllergyWriteProfile : AthenaOneWriteProfile
{
    private const string AllergenToken = "{{allergenid}}";

    private static readonly HashSet<string> NoKnownAllergyCodes =
        new(StringComparer.Ordinal) { "716186003", "409137002", "429625007", "428607008", "428197003" };

    public override string ResourceType => "AllergyIntolerance";

    public override EhrShapeResult Shape(JsonObject source, EhrWriteBackRunOptions options)
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

        var code = Object(source, "code");
        if (Codes(code).Any(NoKnownAllergyCodes.Contains))
        {
            return EhrShapeResult.Skip("no-known-allergies");
        }

        var patient = Reference(source, "patient");
        if (patient is null)
        {
            return EhrShapeResult.Reject("missing-patient");
        }

        if (DisplayName(code) is not { } name)
        {
            return EhrShapeResult.Reject("missing-allergen-name");
        }

        var allergy = new JsonObject { ["allergenid"] = AllergenToken, ["allergenname"] = name };
        if (AthenaDate(String(source, "onsetDateTime")) is { } onset)
        {
            allergy["onsetdate"] = onset;
        }

        if (NoteText(source, 1000) is { } note)
        {
            allergy["note"] = note;
        }

        var reactions = new JsonArray();
        foreach (var reaction in Objects(source, "reaction"))
        {
            foreach (var manifestation in Objects(reaction, "manifestation"))
            {
                if (DisplayName(manifestation) is not { } reactionName)
                {
                    continue;
                }

                var item = new JsonObject { ["reactionname"] = reactionName };
                if (CodeOf(manifestation, SnomedSystem) is { } snomed)
                {
                    item["snomedcode"] = snomed;
                }

                if (String(reaction, "severity") is { } severity)
                {
                    item["severity"] = severity;
                }

                reactions.Add(item);
            }
        }

        if (reactions.Count > 0)
        {
            allergy["reactions"] = reactions;
        }

        var request = Request("allergy", "PUT", $"chart/{R.PatientIdToken}/allergies", department: true, idField: null);
        JsonField(request, "allergies", new JsonArray(allergy));
        Lookup(request, R.AllergenLookup, name, AllergenToken);
        return EhrShapeResult.Shaped(request, patient);
    }
}

/// <summary>athenaOne <c>POST /chart/{patientid}/problems</c>: a SNOMED code is required (athena takes no ICD-10 here),
/// with a start date and a note. Returns <c>problemid</c>. Only active problem-list items are sent.</summary>
public sealed class AthenaOneProblemWriteProfile : AthenaOneWriteProfile
{
    public override string ResourceType => "Condition";

    public override string? Variant => EhrWriteVariants.ProblemListItem;

    public override EhrShapeResult Shape(JsonObject source, EhrWriteBackRunOptions options)
    {
        var verification = FirstCode(source, "verificationStatus");
        if (verification is "entered-in-error" or "refuted")
        {
            return EhrShapeResult.Skip(verification);
        }

        var categories = Objects(source, "category").ToList();
        if (categories.Count > 0 && !categories.Any(c => Codes(c).Contains("problem-list-item")))
        {
            return EhrShapeResult.Skip("not-a-problem-list-item");
        }

        if (FirstCode(source, "clinicalStatus") is "inactive" or "resolved" or "remission"
            || String(source, "abatementDateTime") is not null)
        {
            return EhrShapeResult.Skip("not-active");
        }

        var subject = Reference(source, "subject");
        if (subject is null)
        {
            return EhrShapeResult.Reject("missing-patient");
        }

        if (CodeOf(Object(source, "code"), SnomedSystem) is not { } snomed || !long.TryParse(snomed, out _))
        {
            return EhrShapeResult.Reject("missing-snomed-code");
        }

        var request = Request("problem", "POST", $"chart/{R.PatientIdToken}/problems", department: true, idField: "problemid");
        var fields = Fields(request);
        fields["snomedcode"] = snomed;
        if (AthenaDate(String(source, "onsetDateTime") ?? String(Object(source, "onsetPeriod"), "start")) is { } start)
        {
            fields["startdate"] = start;
        }

        if (NoteText(source, 1000) is { } note)
        {
            fields["note"] = note;
        }

        return EhrShapeResult.Shaped(request, subject);
    }
}

/// <summary>
/// athenaOne <c>POST /chart/encounter/{encounterid}/vitals</c>: readings on an open or in-review encounter, as an array
/// of reading groups (a blood pressure's systolic and diastolic share one group). No ids come back.
/// <para>UNCONFIRMED: the element format inside a group and the <c>VITALS.*</c> clinical element ids below are inferred
/// from athena's GET responses; confirm against <c>GET /chart/configuration/vitals</c> on the preview practice before
/// the connection is activated. Values are sent in the source's units.</para>
/// </summary>
public sealed class AthenaOneVitalSignWriteProfile : AthenaOneWriteProfile
{
    private static readonly IReadOnlyDictionary<string, string> ElementByLoinc = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["8480-6"] = "VITALS.BLOODPRESSURE.SYSTOLIC",
        ["8462-4"] = "VITALS.BLOODPRESSURE.DIASTOLIC",
        ["8867-4"] = "VITALS.PULSE.RATE",
        ["9279-1"] = "VITALS.RESPIRATIONRATE",
        ["8310-5"] = "VITALS.TEMPERATURE",
        ["29463-7"] = "VITALS.WEIGHT",
        ["8302-2"] = "VITALS.HEIGHT",
        ["59408-5"] = "VITALS.O2SATURATION",
        ["2708-6"] = "VITALS.O2SATURATION",
        ["39156-5"] = "VITALS.BMI",
        ["9843-4"] = "VITALS.HEADCIRCUMFERENCE",
    };

    private readonly EpicVitalSignWriteProfile _epic = new();

    public override string ResourceType => "Observation";

    public override string? Variant => EhrWriteVariants.VitalSigns;

    public override EhrShapeResult Shape(JsonObject source, EhrWriteBackRunOptions options)
    {
        var shaped = _epic.Shape(source, options);
        if (shaped.Outcome != EhrShapeOutcome.Shaped || shaped.Resource is not { } vital)
        {
            return shaped;
        }

        var readingTaken = String(vital, "effectiveDateTime");
        var group = new JsonArray();
        if (Array(vital, "component") is { Count: > 0 } components)
        {
            foreach (var component in components.OfType<JsonObject>())
            {
                if (Element(component, readingTaken) is { } element)
                {
                    group.Add(element);
                }
            }
        }
        else if (Element(vital, readingTaken) is { } element)
        {
            group.Add(element);
        }

        if (group.Count == 0)
        {
            return EhrShapeResult.Skip("vital-not-accepted-by-athena");
        }

        var request = Request("vitals", "POST", $"chart/encounter/{R.EncounterIdToken}/vitals", department: false, idField: null);
        JsonField(request, "vitals", new JsonArray(group));
        return EhrShapeResult.Shaped(request, shaped.SourcePatientReference, shaped.SourceEncounterReference);
    }

    private static JsonObject? Element(JsonObject reading, string? readingTaken)
    {
        var loinc = CodeOf(Object(reading, "code"), LoincSystem);
        var quantity = Object(reading, "valueQuantity");
        if (loinc is null || !ElementByLoinc.TryGetValue(loinc, out var element)
            || (quantity?["value"] as JsonValue)?.TryGetValue<decimal>(out var value) != true)
        {
            return null;
        }

        var item = new JsonObject
        {
            ["clinicalelementid"] = element,
            ["value"] = value.ToString(CultureInfo.InvariantCulture),
        };
        if ((String(quantity, "unit") ?? String(quantity, "code")) is { } unit)
        {
            item["unit"] = unit;
        }

        if (readingTaken is not null)
        {
            item["readingtaken"] = readingTaken;
        }

        return item;
    }
}

/// <summary>athenaOne <c>POST /patients/{patientid}/documents/labresult</c> (multipart): one lab result document per
/// final laboratory Observation, as one analyte with its LOINC code, value, units, reference range and abnormal flag.
/// Returns <c>labresultid</c>. Left open for a clinician to review.</summary>
public sealed class AthenaOneLabResultWriteProfile : AthenaOneWriteProfile
{
    private static readonly IReadOnlyDictionary<string, string> AbnormalFlags = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["N"] = "NORMAL",
        ["A"] = "ABNORMAL",
        ["AA"] = "CRITICAL",
        ["H"] = "HIGH",
        ["HH"] = "CRITICAL HIGH",
        ["L"] = "LOW",
        ["LL"] = "CRITICAL LOW",
    };

    public override string ResourceType => "Observation";

    public override string? Variant => EhrWriteVariants.LaboratoryResult;

    public override EhrShapeResult Shape(JsonObject source, EhrWriteBackRunOptions options)
    {
        if (!Objects(source, "category").Any(c => Codes(c).Contains("laboratory")))
        {
            return EhrShapeResult.Skip("not-a-laboratory-result");
        }

        var status = String(source, "status");
        if (status is not ("final" or "amended" or "corrected"))
        {
            return EhrShapeResult.Skip(status is "entered-in-error" or "cancelled" ? status : "not-final");
        }

        var subject = Reference(source, "subject");
        if (subject is null)
        {
            return EhrShapeResult.Reject("missing-patient");
        }

        var code = Object(source, "code");
        if (DisplayName(code) is not { } name)
        {
            return EhrShapeResult.Reject("missing-test-name");
        }

        var analyte = new JsonObject { ["analytename"] = name, ["resultstatus"] = "Final" };
        if (CodeOf(code, LoincSystem) is { } loinc)
        {
            analyte["loinc"] = loinc;
        }

        if (Object(source, "valueQuantity") is { } quantity && (quantity["value"] as JsonValue)?.TryGetValue<decimal>(out var number) == true)
        {
            analyte["value"] = number.ToString(CultureInfo.InvariantCulture);
            if ((String(quantity, "unit") ?? String(quantity, "code")) is { } unit)
            {
                analyte["units"] = unit;
            }
        }
        else if (String(source, "valueString") is { Length: > 0 } text)
        {
            analyte["value"] = text;
        }
        else if (DisplayName(Object(source, "valueCodeableConcept")) is { } coded)
        {
            analyte["value"] = coded;
        }
        else
        {
            return EhrShapeResult.Reject("missing-value");
        }

        if (Objects(source, "referenceRange").FirstOrDefault() is { } range)
        {
            var low = (Object(range, "low")?["value"] as JsonValue)?.ToString();
            var high = (Object(range, "high")?["value"] as JsonValue)?.ToString();
            var rangeText = String(range, "text") ?? (low is not null || high is not null ? $"{low}-{high}" : null);
            if (rangeText is not null)
            {
                analyte["referencerange"] = rangeText;
            }
        }

        if (Objects(source, "interpretation").SelectMany(Codes).FirstOrDefault(AbnormalFlags.ContainsKey) is { } flag)
        {
            analyte["abnormalflag"] = AbnormalFlags[flag];
        }

        if (NoteText(source, 1000) is { } note)
        {
            analyte["note"] = note;
        }

        var request = Request("labresult", "POST", $"patients/{R.PatientIdToken}/documents/labresult", department: true, idField: "labresultid", multipart: true);
        var fields = Fields(request);
        var effective = String(source, "effectiveDateTime") ?? String(Object(source, "effectivePeriod"), "start");
        if (AthenaDate(effective) is { } date)
        {
            fields["observationdate"] = date;
        }

        if (AthenaTime(effective) is { } time)
        {
            fields["observationtime"] = time;
        }

        fields["resultstatus"] = "FINAL";
        if (options.TargetProviderId is { Length: > 0 } provider)
        {
            fields["providerid"] = provider;
        }

        JsonField(request, "analytes", new JsonArray(analyte));
        return EhrShapeResult.Shaped(request, subject);
    }
}

/// <summary>athenaOne <c>POST /chart/{patientid}/medications</c>: the medication is looked up by exact name in athena's
/// medication list (<c>/reference/medications</c>; athena takes no RxNorm here), with start and stop dates and the sig.
/// Returns <c>medicationentryid</c>. Fed from a source MedicationRequest or MedicationStatement (one subclass each).</summary>
public abstract class AthenaOneMedicationWriteProfile : AthenaOneWriteProfile
{
    private const string MedicationToken = "{{medicationid}}";
    private static readonly HashSet<string> NotOnTheList = new(StringComparer.Ordinal) { "entered-in-error", "cancelled", "draft", "not-taken", "unknown" };

    public override EhrShapeResult Shape(JsonObject source, EhrWriteBackRunOptions options)
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

        var concept = Healow.HealowMedicationListWriteProfile.MedicationConcept(source);
        if (concept is null)
        {
            return EhrShapeResult.Reject("medication-not-inline");
        }

        if (DisplayName(concept) is not { } name)
        {
            return EhrShapeResult.Reject("missing-medication-name");
        }

        var request = Request("medication", "POST", $"chart/{R.PatientIdToken}/medications", department: true, idField: "medicationentryid");
        var fields = Fields(request);
        fields["medicationid"] = MedicationToken;
        var period = Object(source, "effectivePeriod") ?? Object(Object(source, "dispenseRequest"), "validityPeriod");
        if (AthenaDate(String(period, "start") ?? String(source, "effectiveDateTime") ?? String(source, "authoredOn")) is { } start)
        {
            fields["startdate"] = start;
        }

        if (AthenaDate(String(period, "end")) is { } stop)
        {
            fields["stopdate"] = stop;
        }

        var dosage = Objects(source, "dosage").FirstOrDefault() ?? Objects(source, "dosageInstruction").FirstOrDefault();
        if (String(dosage, "text") is { Length: > 0 } sig)
        {
            fields["unstructuredsig"] = sig;
        }

        Lookup(request, R.MedicationLookup, name, MedicationToken);
        return EhrShapeResult.Shaped(request, subject);
    }
}

public sealed class AthenaOneMedicationRequestWriteProfile : AthenaOneMedicationWriteProfile
{
    public override string ResourceType => "MedicationRequest";
}

public sealed class AthenaOneMedicationStatementWriteProfile : AthenaOneMedicationWriteProfile
{
    public override string ResourceType => "MedicationStatement";
}

/// <summary>athenaOne <c>POST /chart/{patientid}/vaccines</c>: a historical vaccine by CVX code and administered date,
/// with its NDC when known. Returns <c>vaccineids</c>.</summary>
public sealed class AthenaOneImmunizationWriteProfile : AthenaOneWriteProfile
{
    public override string ResourceType => "Immunization";

    public override EhrShapeResult Shape(JsonObject source, EhrWriteBackRunOptions options)
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
        if (CodeOf(vaccine, CvxSystem) is not { } cvx || !int.TryParse(cvx, out _))
        {
            return EhrShapeResult.Reject("missing-cvx-code");
        }

        if (AthenaDate(String(source, "occurrenceDateTime")) is not { } administered)
        {
            return EhrShapeResult.Reject("missing-occurrence-date");
        }

        var request = Request("vaccine", "POST", $"chart/{R.PatientIdToken}/vaccines", department: true, idField: "vaccineids");
        var fields = Fields(request);
        fields["cvx"] = cvx;
        fields["administerdate"] = administered;
        if (CodeOf(vaccine, NdcSystem) is { } ndc)
        {
            fields["ndc"] = ndc;
        }

        return EhrShapeResult.Shaped(request, patient);
    }
}

/// <summary>
/// athenaOne <c>POST /patients/{patientid}/documents/clinicaldocument</c> (multipart): the note as text
/// (<c>documentdata</c>) in the subclass that fits its type, under the destination's provider when set. Returns
/// <c>clinicaldocumentid</c>. No encounter is needed.
/// <para>UNCONFIRMED: that athena accepts a clinical document with text only and no attachment.</para>
/// </summary>
public sealed class AthenaOneClinicalNoteWriteProfile : AthenaOneWriteProfile
{
    private static readonly IReadOnlySet<string> ExcludedLoincTypes = new HashSet<string>(StringComparer.Ordinal) { "69730-0" };

    private static readonly IReadOnlyDictionary<string, string> SubclassByLoinc = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["18842-5"] = "ADMISSIONDISCHARGE",
        ["11488-4"] = "CONSULTNOTE",
        ["11504-8"] = "OPERATIVENOTE",
    };

    public override string ResourceType => "DocumentReference";

    public override string? Variant => EhrWriteVariants.ClinicalNote;

    public override EhrShapeResult Shape(JsonObject source, EhrWriteBackRunOptions options)
    {
        var (note, problem) = EhrClinicalNote.Parse(source, ExcludedLoincTypes);
        if (note is null)
        {
            return problem!;
        }

        var request = Request("clinicaldocument", "POST", $"patients/{R.PatientIdToken}/documents/clinicaldocument", department: true, idField: "clinicaldocumentid", multipart: true);
        var fields = Fields(request);
        fields["documentsubclass"] = note.LoincCode is { } loinc && SubclassByLoinc.TryGetValue(loinc, out var subclass) ? subclass : "CLINICALDOCUMENT";
        fields["documentdata"] = note.Text;
        if (AthenaDate(note.Date) is { } date)
        {
            fields["observationdate"] = date;
        }

        if (AthenaTime(note.Date) is { } time)
        {
            fields["observationtime"] = time;
        }

        if (options.TargetProviderId is { Length: > 0 } provider)
        {
            fields["providerid"] = provider;
        }

        fields["internalnote"] = $"Imported note: {DisplayName(note.Type) ?? "clinical note"}";
        return EhrShapeResult.Shaped(request, note.Subject, note.SourceEncounter);
    }
}

/// <summary>athenaOne <c>POST /patients</c>: registers a patient in the destination's department (required), from the
/// same demographics subset the other vendors take. Returns <c>patientid</c>. Sent only after the MPI said the patient
/// is not in athena and the destination opted in.</summary>
public sealed class AthenaOnePatientWriteProfile : AthenaOneWriteProfile
{
    private static readonly HashSet<string> SsnSystems = new(StringComparer.OrdinalIgnoreCase)
    {
        "http://hl7.org/fhir/sid/us-ssn", "urn:oid:2.16.840.1.113883.4.1",
    };

    private readonly EpicPatientWriteProfile _demographics = new();

    public override string ResourceType => "Patient";

    public override EhrShapeResult Shape(JsonObject source, EhrWriteBackRunOptions options)
    {
        var shaped = _demographics.Shape(source, options);
        if (shaped.Outcome != EhrShapeOutcome.Shaped || shaped.Resource is not { } patient)
        {
            return shaped;
        }

        if (string.IsNullOrWhiteSpace(options.TargetDepartmentId))
        {
            return EhrShapeResult.Reject("target-department-not-configured");
        }

        var name = Objects(patient, "name").First();
        var request = Request("patient", "POST", "patients", department: false, idField: "patientid");
        var fields = Fields(request);
        fields["departmentid"] = options.TargetDepartmentId;
        fields["firstname"] = Array(name, "given")!.OfType<JsonValue>().First().ToString();
        fields["lastname"] = String(name, "family");
        fields["dob"] = AthenaDate(String(patient, "birthDate"));
        if (String(patient, "gender") switch { "male" => "M", "female" => "F", _ => null } is { } sex)
        {
            fields["sex"] = sex;
        }

        if (Objects(patient, "identifier").FirstOrDefault(i => SsnSystems.Contains(String(i, "system") ?? string.Empty)) is { } ssn)
        {
            fields["ssn"] = String(ssn, "value");
        }

        foreach (var telecom in Objects(patient, "telecom"))
        {
            var target = (String(telecom, "system"), String(telecom, "use")) switch
            {
                ("email", _) => "email",
                ("phone", "mobile") => "mobilephone",
                ("phone", "home") or ("phone", null) => "homephone",
                ("phone", "work") => "workphone",
                _ => null,
            };
            if (target is not null && !fields.ContainsKey(target))
            {
                fields[target] = String(telecom, "value");
            }
        }

        if (Objects(patient, "address").FirstOrDefault() is { } address)
        {
            var lines = Array(address, "line")?.OfType<JsonValue>().Select(v => v.ToString()).ToList() ?? [];
            if (lines.Count > 0)
            {
                fields["address1"] = lines[0];
            }

            if (lines.Count > 1)
            {
                fields["address2"] = lines[1];
            }

            CopyIfString(address, fields, "city");
            CopyIfString(address, fields, "state");
            CopyIfString(address, fields, "postalCode", "zip");
        }

        return EhrShapeResult.Shaped(request, sourcePatientReference: null);
    }

    public override void BindReferences(JsonObject shaped, string targetPatientId, string? targetEncounterId)
    {
        // A new patient references nothing.
    }
}

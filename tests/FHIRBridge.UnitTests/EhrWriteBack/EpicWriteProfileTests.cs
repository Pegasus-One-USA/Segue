using System.Text.Json.Nodes;
using FHIRBridge.Application.Abstractions.Destinations;
using FHIRBridge.Infrastructure.Destinations.EhrWriteBack;
using FHIRBridge.Infrastructure.Destinations.EhrWriteBack.Epic;
using FluentAssertions;

namespace FHIRBridge.UnitTests.EhrWriteBack;

/// <summary>
/// Each Epic write profile against the rules verified in Phase 0 (docs/backend/20-epic-r4-write-back.md section 3):
/// the modifier values Epic accepts, the element subset it files, and the records that must not be sent at all.
/// </summary>
public sealed class EpicWriteProfileTests
{
    private static readonly EhrWriteBackRunOptions Options =
        new(DryRun: true, CreatePatientIfMissing: false, MaxWritesPerRun: 500, NoteDocStatus: "preliminary", ResourceTypes: []);

    private static JsonObject Json(string json) => JsonNode.Parse(json)!.AsObject();

    // ---- AllergyIntolerance (945) ----

    [Fact]
    public void Allergy_is_forced_to_active_and_unconfirmed_and_keeps_one_manifestation_per_reaction()
    {
        var source = Json("""
            {"resourceType":"AllergyIntolerance","id":"a1",
             "clinicalStatus":{"coding":[{"code":"active"}]},
             "verificationStatus":{"coding":[{"code":"confirmed"}]},
             "category":["medication","bogus"],
             "code":{"coding":[{"system":"http://www.nlm.nih.gov/research/umls/rxnorm","code":"7980","display":"penicillin G"},{"system":"x"}],"text":"Penicillin G"},
             "patient":{"reference":"Patient/p1"},
             "recorder":{"reference":"Practitioner/x"},
             "reaction":[{"manifestation":[{"text":"Hives"},{"text":"Rash"}],"severity":"moderate"}]}
            """);

        var result = new EpicAllergyIntoleranceWriteProfile().Shape(source, Options);

        result.Outcome.Should().Be(EhrShapeOutcome.Shaped);
        result.SourcePatientReference.Should().Be("Patient/p1");
        var shaped = result.Resource!;
        shaped["clinicalStatus"]!["coding"]![0]!["code"]!.GetValue<string>().Should().Be("active");
        shaped["verificationStatus"]!["coding"]![0]!["code"]!.GetValue<string>().Should().Be("unconfirmed");
        shaped["category"]!.AsArray().Select(c => c!.GetValue<string>()).Should().Equal("medication");
        shaped["code"]!["coding"]!.AsArray().Should().ContainSingle();
        shaped["reaction"]![0]!["manifestation"]!.AsArray().Should().ContainSingle();
        shaped.ContainsKey("id").Should().BeFalse();
        shaped.ContainsKey("recorder").Should().BeFalse();
        source["verificationStatus"]!["coding"]![0]!["code"]!.GetValue<string>().Should().Be("confirmed", because: "the source must not be mutated");
    }

    [Theory]
    [InlineData("""{"resourceType":"AllergyIntolerance","clinicalStatus":{"coding":[{"code":"resolved"}]},"code":{"text":"x"},"patient":{"reference":"Patient/p1"}}""", "not-active")]
    [InlineData("""{"resourceType":"AllergyIntolerance","verificationStatus":{"coding":[{"code":"entered-in-error"}]},"code":{"text":"x"},"patient":{"reference":"Patient/p1"}}""", "entered-in-error")]
    [InlineData("""{"resourceType":"AllergyIntolerance","code":{"coding":[{"system":"http://snomed.info/sct","code":"716186003"}]},"patient":{"reference":"Patient/p1"}}""", "no-known-allergies")]
    public void Allergies_that_must_not_be_filed_are_skipped(string json, string reason)
    {
        var result = new EpicAllergyIntoleranceWriteProfile().Shape(Json(json), Options);

        result.Outcome.Should().Be(EhrShapeOutcome.Skipped);
        result.Reason.Should().Be(reason);
    }

    [Theory]
    [InlineData("""{"resourceType":"AllergyIntolerance","code":{"text":"x"}}""", "missing-patient")]
    [InlineData("""{"resourceType":"AllergyIntolerance","patient":{"reference":"Patient/p1"}}""", "missing-code")]
    public void Incomplete_allergies_are_rejected(string json, string reason)
    {
        var result = new EpicAllergyIntoleranceWriteProfile().Shape(Json(json), Options);

        result.Outcome.Should().Be(EhrShapeOutcome.Rejected);
        result.Reason.Should().Be(reason);
    }

    // ---- Condition (949) ----

    [Fact]
    public void Problem_is_provisional_with_a_problem_list_category_and_one_note_of_at_most_450_characters()
    {
        var source = Json($$"""
            {"resourceType":"Condition","id":"c1",
             "verificationStatus":{"coding":[{"code":"confirmed"}]},
             "category":[{"coding":[{"code":"problem-list-item"}]}],
             "code":{"coding":[{"system":"http://hl7.org/fhir/sid/icd-10-cm","code":"J30.2","display":"Other seasonal allergic rhinitis"}]},
             "subject":{"reference":"Patient/p1"},
             "onsetPeriod":{"start":"2024-04-15"},
             "note":[{"text":"{{new string('a', 300)}}"},{"text":"{{new string('b', 300)}}"}]}
            """);

        var result = new EpicConditionWriteProfile().Shape(source, Options);

        result.Outcome.Should().Be(EhrShapeOutcome.Shaped);
        var shaped = result.Resource!;
        shaped["verificationStatus"]!["coding"]![0]!["code"]!.GetValue<string>().Should().Be("provisional");
        shaped["clinicalStatus"]!["coding"]![0]!["code"]!.GetValue<string>().Should().Be("active");
        shaped["code"]!["text"]!.GetValue<string>().Should().Be("Other seasonal allergic rhinitis");
        shaped["onsetDateTime"]!.GetValue<string>().Should().Be("2024-04-15");
        shaped["note"]!.AsArray().Should().ContainSingle();
        shaped["note"]![0]!["text"]!.GetValue<string>().Length.Should().Be(450);
    }

    [Fact]
    public void Problem_with_an_abatement_is_filed_as_resolved()
    {
        var source = Json("""
            {"resourceType":"Condition","code":{"coding":[{"code":"J06.9","display":"URI"}]},
             "subject":{"reference":"Patient/p1"},"abatementDateTime":"2025-01-01"}
            """);

        var result = new EpicConditionWriteProfile().Shape(source, Options);

        result.Resource!["clinicalStatus"]!["coding"]![0]!["code"]!.GetValue<string>().Should().Be("resolved");
    }

    [Theory]
    [InlineData("""{"resourceType":"Condition","category":[{"coding":[{"code":"encounter-diagnosis"}]}],"code":{"coding":[{"code":"J06.9","display":"x"}]},"subject":{"reference":"Patient/p1"}}""", "not-a-problem-list-item")]
    [InlineData("""{"resourceType":"Condition","code":{"text":"Feeling unwell"},"subject":{"reference":"Patient/p1"}}""", "text-only-problem")]
    [InlineData("""{"resourceType":"Condition","clinicalStatus":{"coding":[{"code":"inactive"}]},"code":{"coding":[{"code":"J06.9","display":"x"}]},"subject":{"reference":"Patient/p1"}}""", "not-active")]
    public void Problems_that_must_not_be_filed_are_skipped(string json, string reason)
    {
        var result = new EpicConditionWriteProfile().Shape(Json(json), Options);

        result.Outcome.Should().Be(EhrShapeOutcome.Skipped);
        result.Reason.Should().Be(reason);
    }

    [Fact]
    public void Problem_without_any_name_is_rejected()
    {
        var source = Json("""{"resourceType":"Condition","code":{"coding":[{"code":"J06.9"}]},"subject":{"reference":"Patient/p1"}}""");

        new EpicConditionWriteProfile().Shape(source, Options).Reason.Should().Be("missing-problem-name");
    }

    // ---- DocumentReference clinical note (1046) ----

    private const string PlainTextNote = """
        {"resourceType":"DocumentReference","id":"n1","status":"current",
         "type":{"coding":[{"system":"http://loinc.org","code":"11506-3","display":"Progress note"}]},
         "subject":{"reference":"Patient/p1"},
         "author":[{"reference":"Practitioner/x"}],
         "content":[{"attachment":{"contentType":"text/plain; charset=utf-8","data":"aGVsbG8="}},{"attachment":{"contentType":"text/plain","data":"c2Vjb25k"}}],
         "context":{"encounter":[{"reference":"Encounter/e1"}]}}
        """;

    [Fact]
    public void Note_is_preliminary_by_default_without_author_and_with_only_the_first_attachment()
    {
        var result = new EpicClinicalNoteWriteProfile().Shape(Json(PlainTextNote), Options);

        result.Outcome.Should().Be(EhrShapeOutcome.Shaped);
        result.SourceEncounterReference.Should().Be("Encounter/e1");
        var shaped = result.Resource!;
        shaped["docStatus"]!.GetValue<string>().Should().Be("preliminary");
        shaped["status"]!.GetValue<string>().Should().Be("current");
        shaped.ContainsKey("author").Should().BeFalse();
        shaped["content"]!.AsArray().Should().ContainSingle();
        shaped["content"]![0]!["attachment"]!["data"]!.GetValue<string>().Should().Be("aGVsbG8=");
    }

    [Fact]
    public void Note_binds_the_target_patient_and_encounter()
    {
        var profile = new EpicClinicalNoteWriteProfile();
        var shaped = profile.Shape(Json(PlainTextNote), Options).Resource!;

        profile.BindReferences(shaped, "eTargetPatient", "eTargetEncounter");

        shaped["subject"]!["reference"]!.GetValue<string>().Should().Be("Patient/eTargetPatient");
        shaped["context"]!["encounter"]![0]!["reference"]!.GetValue<string>().Should().Be("Encounter/eTargetEncounter");
    }

    [Fact]
    public void Note_status_final_is_used_only_when_the_destination_opts_in()
    {
        var finalOptions = Options with { NoteDocStatus = "final" };

        new EpicClinicalNoteWriteProfile().Shape(Json(PlainTextNote), finalOptions).Resource!["docStatus"]!
            .GetValue<string>().Should().Be("final");
    }

    [Theory]
    [InlineData("text/html", "note-not-plain-text")]
    [InlineData("application/rtf", "note-not-plain-text")]
    public void Notes_epic_cannot_take_are_rejected(string contentType, string reason)
    {
        var json = PlainTextNote.Replace("text/plain; charset=utf-8", contentType);

        new EpicClinicalNoteWriteProfile().Shape(Json(json), Options).Reason.Should().Be(reason);
    }

    [Fact]
    public void Discharge_summary_is_skipped()
    {
        var json = PlainTextNote.Replace("11506-3", "18842-5");

        var result = new EpicClinicalNoteWriteProfile().Shape(Json(json), Options);

        result.Outcome.Should().Be(EhrShapeOutcome.Skipped);
        result.Reason.Should().Be("excluded-note-type");
    }

    // ---- Observation vital sign (963) ----

    private const string Weight = """
        {"resourceType":"Observation","id":"o1","status":"final",
         "category":[{"coding":[{"system":"http://terminology.hl7.org/CodeSystem/observation-category","code":"vital-signs"}]}],
         "code":{"coding":[{"system":"urn:oid:1.2.840.114350","code":"14"},{"system":"http://loinc.org","code":"29463-7","display":"Body weight"}]},
         "subject":{"reference":"Patient/p1"},"encounter":{"reference":"Encounter/e1"},
         "effectiveDateTime":"2026-09-30T11:47:21Z",
         "valueQuantity":{"value":36.2,"unit":"kg","system":"http://unitsofmeasure.org","code":"kg"},
         "performer":[{"reference":"Practitioner/x"}]}
        """;

    [Fact]
    public void Vital_keeps_one_loinc_coding_and_the_epic_category_system()
    {
        var result = new EpicVitalSignWriteProfile().Shape(Json(Weight), Options);

        result.Outcome.Should().Be(EhrShapeOutcome.Shaped);
        var shaped = result.Resource!;
        shaped["code"]!["coding"]!.AsArray().Should().ContainSingle();
        shaped["code"]!["coding"]![0]!["code"]!.GetValue<string>().Should().Be("29463-7");
        shaped["category"]![0]!["coding"]![0]!["system"]!.GetValue<string>().Should().Be("http://hl7.org/fhir/observation-category");
        shaped["valueQuantity"]!["unit"]!.GetValue<string>().Should().Be("kg");
        shaped.ContainsKey("performer").Should().BeFalse();
        result.SourceEncounterReference.Should().Be("Encounter/e1");
    }

    [Theory]
    [InlineData("2026-09-30T11:47:21", "effective-without-timezone")]
    [InlineData("2026-09-30", "effective-without-time")]
    public void Vital_without_a_full_timestamp_is_rejected(string effective, string reason)
    {
        var json = Weight.Replace("2026-09-30T11:47:21Z", effective);

        new EpicVitalSignWriteProfile().Shape(Json(json), Options).Reason.Should().Be(reason);
    }

    [Fact]
    public void Vital_without_a_unit_is_rejected_rather_than_left_to_epics_default()
    {
        var json = Weight.Replace("\"unit\":\"kg\",", string.Empty).Replace(",\"code\":\"kg\"", string.Empty);

        new EpicVitalSignWriteProfile().Shape(Json(json), Options).Reason.Should().Be("missing-unit");
    }

    [Fact]
    public void Laboratory_observation_is_not_sent_to_the_vital_sign_api()
    {
        var json = Weight.Replace("vital-signs", "laboratory");

        var result = new EpicVitalSignWriteProfile().Shape(Json(json), Options);

        result.Outcome.Should().Be(EhrShapeOutcome.Skipped);
        result.Reason.Should().Be("not-a-vital-sign");
    }

    [Fact]
    public void Blood_pressure_is_filed_as_its_two_components()
    {
        var source = Json("""
            {"resourceType":"Observation","status":"final",
             "category":[{"coding":[{"code":"vital-signs"}]}],
             "code":{"coding":[{"system":"http://loinc.org","code":"85354-9"}]},
             "subject":{"reference":"Patient/p1"},"effectiveDateTime":"2026-09-30T11:47:21-05:00",
             "component":[
               {"code":{"coding":[{"system":"http://loinc.org","code":"8480-6"}]},"valueQuantity":{"value":120,"unit":"mmHg"}},
               {"code":{"coding":[{"system":"http://loinc.org","code":"8462-4"}]},"valueQuantity":{"value":80,"unit":"mmHg"}}]}
            """);

        var result = new EpicVitalSignWriteProfile().Shape(source, Options);

        result.Outcome.Should().Be(EhrShapeOutcome.Shaped);
        var components = result.Resource!["component"]!.AsArray();
        components.Should().HaveCount(2);
        components[0]!["valueQuantity"]!["value"]!.GetValue<decimal>().Should().Be(120);
        result.Resource.ContainsKey("valueQuantity").Should().BeFalse();
    }

    // ---- Patient (930) ----

    [Fact]
    public void Patient_has_one_official_name_a_home_address_and_no_epic_fhir_id_identifiers()
    {
        var source = Json("""
            {"resourceType":"Patient","id":"p1",
             "identifier":[
               {"system":"http://open.epic.com/FHIR/StructureDefinition/patient-fhir-id","value":"eABC"},
               {"use":"usual","system":"urn:oid:2.16.840.1.113883.4.1","value":"999-99-9999"},
               {"value":"no-system"}],
             "name":[{"use":"usual","family":"Nick","given":["N"]},{"use":"official","family":"Powell","given":["Desiree","Caroline","Extra"]}],
             "telecom":[{"system":"phone","value":"608-555-0142","use":"home"},{"system":"fax","value":"1"}],
             "gender":"female","birthDate":"2014-11-14",
             "address":[{"use":"work","line":["1 Office"],"city":"Madison"},{"use":"home","line":["1 Probe Way"],"city":"Verona","state":"WI"}],
             "generalPractitioner":[{"reference":"Practitioner/x"}],"link":[{"other":{"reference":"Patient/y"}}]}
            """);

        var result = new EpicPatientWriteProfile().Shape(source, Options);

        result.Outcome.Should().Be(EhrShapeOutcome.Shaped);
        var shaped = result.Resource!;
        shaped["active"]!.GetValue<bool>().Should().BeTrue();
        shaped["name"]!.AsArray().Should().ContainSingle();
        shaped["name"]![0]!["family"]!.GetValue<string>().Should().Be("Powell");
        shaped["name"]![0]!["given"]!.AsArray().Should().HaveCount(2);
        shaped["identifier"]!.AsArray().Should().ContainSingle();
        shaped["telecom"]!.AsArray().Should().ContainSingle();
        shaped["address"]![0]!["city"]!.GetValue<string>().Should().Be("Verona");
        shaped.ContainsKey("generalPractitioner").Should().BeFalse();
        shaped.ContainsKey("link").Should().BeFalse();
    }

    [Theory]
    [InlineData("""{"resourceType":"Patient","gender":"female","birthDate":"2014-11-14"}""", "missing-name")]
    [InlineData("""{"resourceType":"Patient","name":[{"family":"P","given":["D"]}],"birthDate":"2014-11-14"}""", "missing-gender")]
    [InlineData("""{"resourceType":"Patient","name":[{"family":"P","given":["D"]}],"gender":"female","birthDate":"2014"}""", "missing-birth-date")]
    public void Incomplete_patients_are_rejected(string json, string reason)
    {
        new EpicPatientWriteProfile().Shape(Json(json), Options).Reason.Should().Be(reason);
    }

    [Fact]
    public void Deceased_patient_is_never_created()
    {
        var source = Json("""{"resourceType":"Patient","name":[{"family":"P","given":["D"]}],"gender":"female","birthDate":"1950-01-01","deceasedDateTime":"2020-01-01"}""");

        new EpicPatientWriteProfile().Shape(source, Options).Reason.Should().Be("deceased-patient");
    }
}

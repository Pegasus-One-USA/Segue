using System.Text;
using System.Text.Json.Nodes;
using FHIRBridge.Application.Abstractions.Destinations;
using FHIRBridge.Infrastructure.Destinations.EhrWriteBack;
using FHIRBridge.Infrastructure.Destinations.EhrWriteBack.Healow;
using FluentAssertions;

namespace FHIRBridge.UnitTests.EhrWriteBack;

/// <summary>Each eClinicalWorks profile builds exactly what eCW's published Create API schema lists.</summary>
public sealed class HealowWriteProfileTests
{
    private static readonly EhrWriteBackRunOptions Options = new(true, false, 500, "preliminary", [], TargetProviderId: "prov-9");

    private static JsonObject Json(string json) => (JsonObject)JsonNode.Parse(json)!;

    private static string Code(JsonNode? concept, int index = 0) => concept!["coding"]![index]!["code"]!.GetValue<string>();

    [Fact]
    public void A_problem_sends_icd10_before_snomed_and_drops_other_systems()
    {
        var result = new HealowConditionWriteProfile().Shape(Json("""
            {"resourceType":"Condition","subject":{"reference":"Patient/p"},
             "category":[{"coding":[{"code":"problem-list-item"}]}],
             "code":{"coding":[{"system":"http://snomed.info/sct","code":"44054006"},{"system":"urn:oid:1.2.840.114350","code":"X"},
                               {"system":"http://hl7.org/fhir/sid/icd-10-cm","code":"E11.9"}],"text":"Diabetes"}}
            """), Options);

        result.Outcome.Should().Be(EhrShapeOutcome.Shaped);
        var code = result.Resource!["code"]!;
        code["coding"]!.AsArray().Should().HaveCount(2);
        Code(code, 0).Should().Be("E11.9");
        Code(code, 1).Should().Be("44054006");
    }

    [Theory]
    [InlineData("""{"coding":[{"code":"resolved"}]}""", null, "resolved-without-abatement-date")]
    [InlineData("""{"coding":[{"code":"resolved"}]}""", "2020-01-01", null)]
    [InlineData(null, "2020-01-01", null)]
    public void A_resolved_problem_needs_its_abatement_date(string? clinical, string? abatement, string? rejection)
    {
        var source = Json("""
            {"resourceType":"Condition","subject":{"reference":"Patient/p"},
             "code":{"coding":[{"system":"http://hl7.org/fhir/sid/icd-10-cm","code":"J45"}],"text":"Asthma"}}
            """);
        if (clinical is not null)
        {
            source["clinicalStatus"] = JsonNode.Parse(clinical);
        }

        if (abatement is not null)
        {
            source["abatementDateTime"] = abatement;
        }

        var result = new HealowConditionWriteProfile().Shape(source, Options);

        if (rejection is null)
        {
            result.Outcome.Should().Be(EhrShapeOutcome.Shaped);
            Code(result.Resource!["clinicalStatus"]).Should().Be("resolved");
        }
        else
        {
            result.Reason.Should().Be(rejection);
        }
    }

    [Fact]
    public void A_problem_coded_only_in_a_local_system_is_rejected()
    {
        new HealowConditionWriteProfile().Shape(Json("""
            {"resourceType":"Condition","subject":{"reference":"Patient/p"},"code":{"coding":[{"system":"urn:local","code":"1"}],"text":"x"}}
            """), Options).Reason.Should().Be("missing-icd10-or-snomed-code");
    }

    [Fact]
    public void Medical_history_sends_text_only_on_the_bound_encounter()
    {
        var profile = new HealowMedicalHistoryWriteProfile();
        var result = profile.Shape(Json("""
            {"resourceType":"Condition","subject":{"reference":"Patient/p"},"category":[{"coding":[{"code":"medical-history"}]}],
             "code":{"coding":[{"system":"http://snomed.info/sct","code":"195967001","display":"Asthma"}]}}
            """), Options);
        ((IEhrWriteProfile)profile).BindReferences(result.Resource!, "ecw-p", "tel-1");

        result.Resource!["code"]!.ToJsonString().Should().Be("""{"text":"Asthma"}""");
        Code(result.Resource["category"]![0]).Should().Be("435871000124102");
        result.Resource["encounter"]!["reference"]!.GetValue<string>().Should().Be("Encounter/tel-1");
        result.Resource["subject"]!["reference"]!.GetValue<string>().Should().Be("Patient/ecw-p");
    }

    [Fact]
    public void Surgical_history_takes_completed_surgical_procedures_with_a_year_and_month()
    {
        var profile = new HealowSurgicalHistoryWriteProfile();
        var surgical = profile.Shape(Json("""
            {"resourceType":"Procedure","status":"completed","subject":{"reference":"Patient/p"},
             "category":{"coding":[{"system":"http://snomed.info/sct","code":"387713003"}]},
             "code":{"text":"Appendectomy"},"performedDateTime":"2019-06-14T09:00:00Z"}
            """), Options);
        var bloodDraw = profile.Shape(Json("""
            {"resourceType":"Procedure","status":"completed","subject":{"reference":"Patient/p"},"code":{"text":"Venipuncture"}}
            """), Options);

        surgical.Resource!["performedDateTime"]!.GetValue<string>().Should().Be("2019-06");
        bloodDraw.Reason.Should().Be("not-a-surgical-procedure");
    }

    [Fact]
    public void An_immunization_needs_a_cvx_code_and_is_sent_as_historical()
    {
        var profile = new HealowImmunizationWriteProfile();
        var withCvx = profile.Shape(Json("""
            {"resourceType":"Immunization","status":"completed","patient":{"reference":"Patient/p"},"occurrenceDateTime":"2021-04-01",
             "vaccineCode":{"coding":[{"system":"http://hl7.org/fhir/sid/cvx","code":"208","display":"COVID-19"}]}}
            """), Options);
        var withoutCvx = profile.Shape(Json("""
            {"resourceType":"Immunization","status":"completed","patient":{"reference":"Patient/p"},"occurrenceDateTime":"2021-04-01",
             "vaccineCode":{"coding":[{"system":"urn:local","code":"F1"}]}}
            """), Options);

        withCvx.Resource!["primarySource"]!.GetValue<bool>().Should().BeFalse();
        Code(withCvx.Resource["vaccineCode"]).Should().Be("208");
        withoutCvx.Reason.Should().Be("missing-cvx-code");
    }

    [Fact]
    public void A_medication_request_becomes_an_ecw_medication_statement_with_rxnorm_and_text()
    {
        var result = new HealowMedicationRequestWriteProfile().Shape(Json("""
            {"resourceType":"MedicationRequest","status":"active","intent":"order","subject":{"reference":"Patient/p"},
             "authoredOn":"2024-02-01",
             "medicationCodeableConcept":{"coding":[{"system":"http://www.nlm.nih.gov/research/umls/rxnorm","code":"860975","display":"Metformin 500 MG"}]},
             "dosageInstruction":[{"text":"1 tab twice daily","route":{"text":"oral"}}]}
            """), Options);

        var shaped = result.Resource!;
        shaped["resourceType"]!.GetValue<string>().Should().Be("MedicationStatement");
        shaped["medicationCodeableConcept"]!["text"]!.GetValue<string>().Should().Be("Metformin 500 MG");
        shaped["effectivePeriod"]!["start"]!.GetValue<string>().Should().Be("2024-02-01");
        shaped["dosage"]![0]!["route"]!["text"]!.GetValue<string>().Should().Be("oral");
    }

    [Theory]
    [InlineData("""{"coding":[{"system":"urn:local","code":"1"}],"text":"X"}""", "missing-rxnorm-or-ndc-code")]
    [InlineData("""{"coding":[{"system":"http://hl7.org/fhir/sid/ndc","code":"0002-1"}]}""", "missing-medication-name")]
    public void A_medication_needs_rxnorm_or_ndc_and_a_name(string concept, string reason)
    {
        var source = Json("""{"resourceType":"MedicationStatement","status":"active","subject":{"reference":"Patient/p"}}""");
        source["medicationCodeableConcept"] = JsonNode.Parse(concept);

        new HealowMedicationStatementWriteProfile().Shape(source, Options).Reason.Should().Be(reason);
    }

    [Fact]
    public void A_contained_medication_is_read_but_an_external_one_is_not_followed()
    {
        var contained = Json("""
            {"resourceType":"MedicationRequest","status":"active","intent":"order","subject":{"reference":"Patient/p"},
             "contained":[{"resourceType":"Medication","id":"m","code":{"coding":[{"system":"http://www.nlm.nih.gov/research/umls/rxnorm","code":"1"}],"text":"Drug"}}],
             "medicationReference":{"reference":"#m"}}
            """);
        var external = Json("""
            {"resourceType":"MedicationRequest","status":"active","intent":"order","subject":{"reference":"Patient/p"},
             "medicationReference":{"reference":"Medication/m"}}
            """);

        new HealowMedicationRequestWriteProfile().Shape(contained, Options).Outcome.Should().Be(EhrShapeOutcome.Shaped);
        new HealowMedicationRequestWriteProfile().Shape(external, Options).Reason.Should().Be("medication-not-inline");
    }

    [Fact]
    public void A_vital_ecw_does_not_list_is_skipped_and_blood_pressure_components_carry_ucum()
    {
        var profile = new HealowVitalSignWriteProfile();
        var bp = profile.Shape(Json("""
            {"resourceType":"Observation","status":"final","category":[{"coding":[{"code":"vital-signs"}]}],"subject":{"reference":"Patient/p"},
             "effectiveDateTime":"2024-05-01T10:00:00Z","code":{"coding":[{"system":"http://loinc.org","code":"85354-9"}]},
             "component":[{"code":{"coding":[{"system":"http://loinc.org","code":"8480-6"}]},"valueQuantity":{"value":120}},
                          {"code":{"coding":[{"system":"http://loinc.org","code":"8462-4"}]},"valueQuantity":{"value":80}}]}
            """), Options);
        var waist = profile.Shape(Json("""
            {"resourceType":"Observation","status":"final","category":[{"coding":[{"code":"vital-signs"}]}],"subject":{"reference":"Patient/p"},
             "effectiveDateTime":"2024-05-01T10:00:00Z","code":{"coding":[{"system":"http://loinc.org","code":"8280-0"}]},
             "valueQuantity":{"value":80,"unit":"cm"}}
            """), Options);

        bp.Resource!["component"]![0]!["valueQuantity"]!["system"]!.GetValue<string>().Should().Be("http://unitsofmeasure.org");
        bp.Resource.ContainsKey("encounter").Should().BeFalse();
        waist.Reason.Should().Be("vital-not-accepted-by-ecw");
    }

    [Fact]
    public void A_patient_needs_an_identifier_and_uses_a_usual_name()
    {
        var profile = new HealowPatientWriteProfile();
        var shaped = profile.Shape(Json("""
            {"resourceType":"Patient","identifier":[{"system":"urn:oid:1","value":"A1"}],"name":[{"family":"Doe","given":["Jo"]}],
             "gender":"other","birthDate":"1990-01-01"}
            """), Options);
        var noIdentifier = profile.Shape(Json("""
            {"resourceType":"Patient","name":[{"family":"Doe","given":["Jo"]}],"gender":"female","birthDate":"1990-01-01"}
            """), Options);

        shaped.Resource!["name"]![0]!["use"]!.GetValue<string>().Should().Be("usual");
        shaped.Resource["gender"]!.GetValue<string>().Should().Be("unknown");
        shaped.Resource.ContainsKey("active").Should().BeFalse();
        noIdentifier.Reason.Should().Be("missing-identifier");
    }

    [Fact]
    public void A_note_travels_as_an_hl7_oru_on_the_encounter_with_the_configured_author()
    {
        var profile = new HealowClinicalNoteWriteProfile();
        var note = Json($$$"""
            {"resourceType":"DocumentReference","status":"current","subject":{"reference":"Patient/p"},"date":"2024-03-05T10:00:00Z",
             "type":{"coding":[{"system":"http://loinc.org","code":"34117-2"}]},
             "content":[{"attachment":{"contentType":"text/plain","data":"{{{Convert.ToBase64String(Encoding.UTF8.GetBytes("BP 120|80\nfollow up"))}}}"}}]}
            """);

        var result = profile.Shape(note, Options);
        profile.BindReferences(result.Resource!, "ecw-p", "enc-7");

        var shaped = result.Resource!;
        shaped["author"]![0]!["reference"]!.GetValue<string>().Should().Be("Practitioner/prov-9");
        shaped["context"]!["encounter"]![0]!["reference"]!.GetValue<string>().Should().Be("Encounter/enc-7");
        shaped.ContainsKey("_plainText").Should().BeFalse(because: "the working text is never sent");
        var attachment = shaped["content"]![0]!["attachment"]!;
        attachment["contentType"]!.GetValue<string>().Should().Be("text/hl7v2");
        var message = Encoding.UTF8.GetString(Convert.FromBase64String(attachment["data"]!.GetValue<string>()));
        message.Should().StartWith("MSH|^~\\&|PROG|PROG|PROG||20240305||ORU^R01|");
        message.Should().Contain("\rOBR|1|enc-7||HPI^HPI\r");
        message.Should().Contain("OBX|1|TX|||BP 120\\F\\80\\.br\\follow up\r");
    }

    [Fact]
    public void The_note_message_is_the_same_on_every_run()
    {
        // The ledger's content hash is taken after binding: a changing control id would read as an edited note.
        HealowClinicalNoteWriteProfile.BuildOruMessage("p", "e", "HPI", "text", "2024-01-01")
            .Should().Be(HealowClinicalNoteWriteProfile.BuildOruMessage("p", "e", "HPI", "text", "2024-01-01"));
    }

    [Fact]
    public void A_note_without_a_configured_author_is_rejected()
    {
        var note = Json($$$"""
            {"resourceType":"DocumentReference","status":"current","subject":{"reference":"Patient/p"},
             "type":{"coding":[{"system":"http://loinc.org","code":"11506-3"}]},
             "content":[{"attachment":{"contentType":"text/plain","data":"{{{Convert.ToBase64String("x"u8.ToArray())}}}"}}]}
            """);

        new HealowClinicalNoteWriteProfile().Shape(note, Options with { TargetProviderId = null }).Reason.Should().Be("note-author-not-configured");
    }
}

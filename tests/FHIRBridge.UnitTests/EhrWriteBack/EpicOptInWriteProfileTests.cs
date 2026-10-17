using System.Text.Json.Nodes;
using FHIRBridge.Application.Abstractions.Destinations;
using FHIRBridge.Domain.Enums;
using FHIRBridge.Domain.Fhir;
using FHIRBridge.Infrastructure.Destinations.EhrWriteBack;
using FHIRBridge.Infrastructure.Destinations.EhrWriteBack.Epic;
using FHIRBridge.Infrastructure.Destinations.EhrWriteBack.GenericFhir;
using FluentAssertions;

namespace FHIRBridge.UnitTests.EhrWriteBack;

/// <summary>
/// Epic's opt-in create APIs (Phase 7), each shaped to its spec, and the Generic FHIR plain-create profile. A record of
/// another variant is skipped with a not-a-/not-an- reason so the writer tries the type's next API.
/// </summary>
public sealed class EpicOptInWriteProfileTests
{
    private static readonly EhrWriteBackRunOptions Options = new(true, false, 500, "preliminary", []);

    private static JsonObject Json(string json) => JsonNode.Parse(json)!.AsObject();

    private static string Text(JsonNode? node) => node!.GetValue<string>();

    [Fact]
    public void Every_epic_capability_has_exactly_one_profile_for_its_variant()
    {
        var registry = new EhrWriteProfileRegistry(
        [
            new EpicAllergyIntoleranceWriteProfile(), new EpicConditionWriteProfile(), new EpicClinicalNoteWriteProfile(),
            new EpicVitalSignWriteProfile(), new EpicPatientWriteProfile(), new EpicLinesDrainsAirwaysWriteProfile(),
            new EpicImagingCharacteristicsWriteProfile(), new EpicRadiotherapyVolumeWriteProfile(),
            new EpicRadiotherapySummaryProcedureWriteProfile(), new EpicRadiotherapySummaryServiceRequestWriteProfile(),
            new EpicDocumentInformationWriteProfile(), new EpicNonPatientDocumentWriteProfile(),
            new EpicCommunityResourceMessageWriteProfile(), new EpicPatientEnteredQuestionnaireWriteProfile(),
        ]);

        foreach (var capability in EhrWriteCapabilities.For(SourceSystemType.Epic))
        {
            var profile = registry.Find(SourceSystemType.Epic, capability.ResourceType, capability.Variant);
            profile.Should().NotBeNull(because: capability.VendorApiId);
            if (capability.Variant is not null && EhrWriteCapabilities.FindAll(SourceSystemType.Epic, capability.ResourceType).Count > 1)
            {
                profile!.Variant.Should().Be(capability.Variant, because: capability.VendorApiId);
            }
        }
    }

    [Fact]
    public void Lines_drains_and_airways_go_final_with_epics_category_and_without_the_source_parent()
    {
        var source = Json("""
            {"resourceType":"Observation","status":"amended",
             "extension":[{"url":"http://hl7.org/fhir/StructureDefinition/observation-sequelTo","valueReference":{"reference":"Observation/parent"}}],
             "category":[{"coding":[{"code":"lda"}]}],"code":{"coding":[{"system":"urn:oid:1.2.840.114350","code":"911010659"}]},
             "subject":{"reference":"Patient/p1"},"encounter":{"reference":"Encounter/e1"},"effectiveDateTime":"2026-10-01T08:00:00Z",
             "note":[{"text":"Placed in left forearm"}]}
            """);

        var result = new EpicLinesDrainsAirwaysWriteProfile().Shape(source, Options);

        result.Outcome.Should().Be(EhrShapeOutcome.Shaped);
        result.SourceEncounterReference.Should().Be("Encounter/e1");
        var shaped = result.Resource!;
        Text(shaped["status"]).Should().Be("final");
        Text(shaped["category"]![0]!["coding"]![0]!["system"]).Should().Be("http://open.epic.com/FHIR/StructureDefinition/observation-category");
        Text(shaped["effectivePeriod"]!["start"]).Should().Be("2026-10-01T08:00:00Z");
        shaped.ContainsKey("extension").Should().BeFalse();
    }

    [Fact]
    public void A_vital_sign_is_not_a_line_and_an_lda_is_not_a_vital_sign()
    {
        var vital = Json("""{"resourceType":"Observation","status":"final","category":[{"coding":[{"code":"vital-signs"}]}],"code":{"coding":[{"system":"http://loinc.org","code":"29463-7"}]},"subject":{"reference":"Patient/p1"}}""");
        var lda = Json("""{"resourceType":"Observation","status":"final","category":[{"coding":[{"code":"LDA"}]}],"code":{"coding":[{"code":"1"}]},"subject":{"reference":"Patient/p1"}}""");

        new EpicLinesDrainsAirwaysWriteProfile().Shape(vital, Options).Reason.Should().Be("not-a-line-drain-airway");
        new EpicVitalSignWriteProfile().Shape(lda, Options).Reason.Should().Be("not-a-vital-sign");
        new EpicImagingCharacteristicsWriteProfile().Shape(lda, Options).Reason.Should().Be("not-an-imaging-characteristic");
    }

    [Fact]
    public void An_imaging_characteristic_needs_the_target_report_it_is_about()
    {
        const string Base = """
            {"resourceType":"Observation","status":"final","category":[{"coding":[{"code":"imaging"}]}],
             "code":{"coding":[{"system":"http://loinc.org","code":"96914-7"}]},"subject":{"reference":"Patient/p1"},
             "effectiveDateTime":"2026-10-01T08:00:00Z",
             "component":[{"code":{"coding":[{"system":"http://loinc.org","code":"96913-9"}]},"valueQuantity":{"value":12.5,"unit":"mGy"}},
                          {"code":{"coding":[{"system":"http://loinc.org","code":"1-1"}]},"valueQuantity":{"value":1}}]
            """;

        new EpicImagingCharacteristicsWriteProfile().Shape(Json(Base + "}"), Options).Reason.Should().Be("missing-imaging-report");
        var shaped = new EpicImagingCharacteristicsWriteProfile().Shape(Json(Base + ""","focus":[{"reference":"DiagnosticReport/dr1"}]}"""), Options).Resource!;

        Text(shaped["focus"]![0]!["reference"]).Should().Be("DiagnosticReport/dr1");
        shaped["component"]!.AsArray().Should().ContainSingle(because: "only Epic's two component codes are sent");
    }

    [Fact]
    public void A_radiotherapy_volume_needs_two_used_identifiers_and_its_last_updated_time()
    {
        const string Volume = """
            {"resourceType":"BodyStructure","meta":{"lastUpdated":"2026-09-01T10:00:00Z"},
             "identifier":[{"use":"official","system":"urn:dicom:uid","value":"1.2.3"},{"use":"usual","value":"PTV_Prostate"}],
             "morphology":{"coding":[{"system":"http://snomed.info/sct","code":"228793007"}]},
             "location":{"coding":[{"system":"http://snomed.info/sct","code":"41216001"}]},"patient":{"reference":"Patient/p1"}}
            """;

        var shaped = new EpicRadiotherapyVolumeWriteProfile().Shape(Json(Volume), Options).Resource!;

        Text(shaped["meta"]!["profile"]![0]).Should().Be(EpicRadiotherapyVolumeWriteProfile.VolumeProfile);
        Text(shaped["meta"]!["lastUpdated"]).Should().Be("2026-09-01T10:00:00Z");
        shaped["identifier"]!.AsArray().Should().HaveCount(2);
        new EpicRadiotherapyVolumeWriteProfile().Shape(Json(Volume.Replace("""{"use":"usual","value":"PTV_Prostate"}""", """{"value":"x"}""")), Options)
            .Reason.Should().Be("missing-identifiers");
        new EpicRadiotherapyVolumeWriteProfile().Shape(Json(Volume.Replace("\"meta\":{\"lastUpdated\":\"2026-09-01T10:00:00Z\"},", string.Empty)), Options)
            .Reason.Should().Be("missing-last-updated");
    }

    private const string Summary = """
        {"resourceType":"%TYPE%","meta":{"lastUpdated":"2026-09-01T10:00:00Z","profile":["http://hl7.org/fhir/us/codex-radiation-therapy/StructureDefinition/codexrt-radiotherapy-course-summary"]},
         "extension":[{"url":"http://hl7.org/fhir/us/mcode/StructureDefinition/mcode-procedure-intent","valueCodeableConcept":{"text":"Curative"}},
                      {"url":"http://hl7.org/fhir/us/mcode/StructureDefinition/mcode-radiotherapy-dose-delivered-to-volume","extension":[{"url":"volume","valueReference":{"reference":"BodyStructure/v1"}}]}],
         "identifier":[{"use":"official","value":"C1"},{"use":"usual","value":"Prostate course"}],"status":"completed","intent":"filler-order",
         "category":[{"coding":[{"system":"http://snomed.info/sct","code":"1287742003"}]}],
         "code":{"coding":[{"system":"http://snomed.info/sct","code":"1217123003"}]},"subject":{"reference":"Patient/p1"},
         "reasonReference":[{"reference":"Condition/c1"}],"reasonCode":[{"coding":[{"system":"http://snomed.info/sct","code":"399068003"}]}],
         "performedPeriod":{"start":"2026-08-01"},"occurrencePeriod":{"start":"2026-08-01"}}
        """;

    [Fact]
    public void A_radiotherapy_summary_keeps_its_extensions_except_those_naming_source_records()
    {
        var procedure = new EpicRadiotherapySummaryProcedureWriteProfile().Shape(Json(Summary.Replace("%TYPE%", "Procedure")), Options).Resource!;
        var order = new EpicRadiotherapySummaryServiceRequestWriteProfile().Shape(Json(Summary.Replace("%TYPE%", "ServiceRequest")), Options).Resource!;

        procedure["extension"]!.AsArray().Should().ContainSingle();
        procedure.ContainsKey("reasonReference").Should().BeFalse();
        Text(procedure["performedPeriod"]!["start"]).Should().Be("2026-08-01");
        procedure.ContainsKey("intent").Should().BeFalse();
        Text(order["intent"]).Should().Be("filler-order");
        Text(order["occurrencePeriod"]!["start"]).Should().Be("2026-08-01");
        new EpicRadiotherapySummaryServiceRequestWriteProfile().Shape(Json(Summary.Replace("%TYPE%", "ServiceRequest").Replace("filler-order", "plan")), Options)
            .Reason.Should().Be("intent-not-supported");
    }

    [Fact]
    public void A_radiotherapy_procedure_has_one_category_as_r4_defines_it_and_is_recognised_by_it()
    {
        var r4Procedure = Summary.Replace("%TYPE%", "Procedure")
            .Replace("\"category\":[{\"coding\":[{\"system\":\"http://snomed.info/sct\",\"code\":\"1287742003\"}]}]",
                     "\"category\":{\"coding\":[{\"system\":\"http://snomed.info/sct\",\"code\":\"1287742003\"}]}");
        r4Procedure.Should().NotContain("\"category\":[");

        var procedure = new EpicRadiotherapySummaryProcedureWriteProfile().Shape(Json(r4Procedure), Options).Resource!;
        var order = new EpicRadiotherapySummaryServiceRequestWriteProfile().Shape(Json(Summary.Replace("%TYPE%", "ServiceRequest")), Options).Resource!;

        procedure["category"].Should().BeOfType<JsonObject>();
        Text(procedure["category"]!["coding"]![0]!["code"]).Should().Be("1287742003");
        order["category"].Should().BeOfType<JsonArray>();
    }

    [Fact]
    public void Scan_metadata_needs_every_element_epic_requires_and_drops_the_scanning_user()
    {
        const string Scan = """
            {"resourceType":"DocumentReference","identifier":[{"system":"urn:scan","value":"S-1"}],"status":"current",
             "type":{"coding":[{"system":"urn:oid:1.2.840.114350","code":"100001"}]},
             "category":[{"coding":[{"system":"http://open.epic.com/FHIR/StructureDefinition/documentreference-category","code":"document-information"}]}],
             "subject":{"reference":"Patient/p1"},"date":"2026-10-01T09:00:00Z","description":"Referral letter",
             "author":[{"reference":"Practitioner/scanner"}],"custodian":{"reference":"Organization/o1","display":"Main Clinic"},
             "context":{"period":{"start":"2026-09-30"},"encounter":[{"reference":"Encounter/e1"}]}}
            """;

        var shaped = new EpicDocumentInformationWriteProfile().Shape(Json(Scan), Options).Resource!;

        shaped.ContainsKey("author").Should().BeFalse();
        shaped["custodian"]!.ToJsonString().Should().Be("""{"display":"Main Clinic"}""");
        shaped["context"]!.ToJsonString().Should().Be("""{"period":{"start":"2026-09-30"}}""");
        new EpicDocumentInformationWriteProfile().Shape(Json(Scan.Replace("\"description\":\"Referral letter\",", string.Empty)), Options)
            .Reason.Should().Be("missing-description");
        new EpicClinicalNoteWriteProfile().Shape(Json(Scan), Options).Reason.Should().Be("not-a-clinical-note");
    }

    [Fact]
    public void A_non_patient_document_is_linked_to_its_target_record_and_has_no_patient()
    {
        var source = Json("""
            {"resourceType":"DocumentReference","identifier":[{"value":"N-1"}],"status":"current","type":{"text":"Contract"},
             "category":[{"coding":[{"system":"http://open.epic.com/FHIR/StructureDefinition/documentreference-category","code":"nonpatient-document-information"}]}],
             "context":{"related":[{"reference":"Contract/k1"},{"reference":"Patient/p1"}]}}
            """);

        var result = new EpicNonPatientDocumentWriteProfile().Shape(source, Options);

        result.SourcePatientReference.Should().BeNull();
        result.Resource!["context"]!["related"]!.AsArray().Should().ContainSingle();
        Text(result.Resource["context"]!["related"]![0]!["reference"]).Should().Be("Contract/k1");
    }

    [Fact]
    public void A_community_resource_message_is_in_progress_on_its_referral()
    {
        const string Message = """
            {"resourceType":"Communication","status":"in-progress","basedOn":[{"reference":"ServiceRequest/ref1"}],
             "subject":{"reference":"Patient/p1"},"encounter":{"reference":"Encounter/e1"},"sent":"2026-10-01T09:00:00Z",
             "sender":{"reference":"Organization/food-bank"},"recipient":[{"reference":"Practitioner/doc1"}],
             "payload":[{"contentString":"Client enrolled"}]}
            """;

        var result = new EpicCommunityResourceMessageWriteProfile().Shape(Json(Message), Options);

        result.Outcome.Should().Be(EhrShapeOutcome.Shaped);
        Text(result.Resource!["basedOn"]![0]!["reference"]).Should().Be("ServiceRequest/ref1");
        result.SourceEncounterReference.Should().Be("Encounter/e1");
        new EpicCommunityResourceMessageWriteProfile().Shape(Json(Message.Replace("in-progress", "completed")), Options).Reason.Should().Be("not-in-progress");
        new EpicCommunityResourceMessageWriteProfile().Shape(Json(Message.Replace("ServiceRequest/ref1", "Task/t1")), Options)
            .Reason.Should().Be("not-a-community-resource-message");
    }

    [Fact]
    public void A_questionnaire_without_its_assignment_is_refused()
    {
        var source = Json("""{"resourceType":"QuestionnaireResponse","status":"completed","subject":{"reference":"Patient/p1"},"questionnaire":"Questionnaire/q","item":[{"linkId":"1","answer":[{"valueBoolean":true}]}]}""");

        new EpicPatientEnteredQuestionnaireWriteProfile().Shape(source, Options).Reason.Should().Be("missing-questionnaire-assignment");
    }

    private static string Questionnaire(string items) =>
        $$$"""{"resourceType":"QuestionnaireResponse","status":"completed","subject":{"identifier":{"system":"urn:epic:assignment","value":"A-1"}},"questionnaire":"Q-9","item":{{{items}}}}""";

    // PR #244 review: an answer Epic cannot take stops the whole response; it is never filed as complete without it.
    [Fact]
    public void A_questionnaire_with_a_coded_answer_is_skipped_whole_not_filed_without_it()
    {
        var source = Json(Questionnaire("""
            [{"linkId":"1","answer":[{"valueCoding":{"system":"http://loinc.org","code":"LA6568-5"}}]},
             {"linkId":"total","answer":[{"valueInteger":7}]}]
            """));

        var result = new EpicPatientEnteredQuestionnaireWriteProfile().Shape(source, Options);

        result.Outcome.Should().Be(EhrShapeOutcome.Skipped);
        result.Reason.Should().Be("unsupported-answer");
    }

    [Fact]
    public void A_questionnaire_keeps_the_answers_of_group_and_follow_up_items()
    {
        var source = Json(Questionnaire("""
            [{"linkId":"g1","item":[{"linkId":"1","answer":[{"valueInteger":2}]},{"linkId":"unanswered"}]},
             {"linkId":"2","answer":[{"valueBoolean":true,"item":[{"linkId":"2.1","answer":[{"valueString":"daily"}]}]}]}]
            """));

        var shaped = new EpicPatientEnteredQuestionnaireWriteProfile().Shape(source, Options).Resource!;

        shaped["item"]!.ToJsonString().Should().Be(
            """[{"linkId":"g1","item":[{"linkId":"1","answer":[{"valueDecimal":2}]}]},{"linkId":"2","answer":[{"valueBoolean":true,"item":[{"linkId":"2.1","answer":[{"valueString":"daily"}]}]}]}]""");
        Text(shaped["status"]).Should().Be("completed");
    }

    [Fact]
    public void A_questionnaire_with_a_coded_answer_in_a_group_is_skipped_whole()
    {
        var source = Json(Questionnaire("""
            [{"linkId":"g1","item":[{"linkId":"1","answer":[{"valueCoding":{"code":"x"}}]}]},{"linkId":"2","answer":[{"valueString":"ok"}]}]
            """));

        new EpicPatientEnteredQuestionnaireWriteProfile().Shape(source, Options).Reason.Should().Be("unsupported-answer");
    }

    // ---- Generic FHIR ----

    [Fact]
    public void A_generic_fhir_create_keeps_the_record_and_drops_what_names_the_source()
    {
        var source = Json("""
            {"resourceType":"DocumentReference","id":"d1","meta":{"versionId":"2","profile":["http://hl7.org/fhir/us/core/StructureDefinition/us-core-documentreference"]},
             "text":{"status":"generated","div":"<div/>"},"status":"current","subject":{"reference":"Patient/p1"},
             "author":[{"reference":"Practitioner/x"}],"contained":[{"resourceType":"Practitioner","id":"c1"}],"custodian":{"reference":"#c1"},
             "context":{"encounter":[{"reference":"Encounter/e1"}],"period":{"start":"2026-10-01"}},
             "content":[{"attachment":{"contentType":"text/plain","data":"aGk="}}]}
            """);

        var result = new GenericFhirWriteProfile("DocumentReference").Shape(source, Options);

        var shaped = result.Resource!;
        shaped.Select(p => p.Key).First().Should().Be("resourceType");
        shaped.ContainsKey("id").Should().BeFalse();
        shaped.ContainsKey("text").Should().BeFalse();
        shaped["meta"]!.ToJsonString().Should().Be("""{"profile":["http://hl7.org/fhir/us/core/StructureDefinition/us-core-documentreference"]}""");
        shaped["author"]!.ToJsonString().Should().Be("""[{"display":"Practitioner reference not transferred"}]""");
        Text(shaped["custodian"]!["reference"]).Should().Be("#c1");
        shaped["context"]!.ToJsonString().Should().Be("""{"period":{"start":"2026-10-01"}}""");
        result.SourcePatientReference.Should().Be("Patient/p1");
    }

    [Theory]
    [InlineData("""{"resourceType":"Condition","verificationStatus":{"coding":[{"code":"entered-in-error"}]},"subject":{"reference":"Patient/p1"}}""", "entered-in-error")]
    [InlineData("""{"resourceType":"Condition","code":{"text":"x"}}""", "missing-patient")]
    public void A_generic_fhir_create_skips_or_refuses_what_it_cannot_file(string json, string reason)
    {
        new GenericFhirWriteProfile("Condition").Shape(Json(json), Options).Reason.Should().Be(reason);
    }

    // PR #244 review: a relative Binary/<id> resolves against the TARGET's base, where it names another record (possibly
    // another patient's); an absolute one is a link into the source server the target cannot follow.
    [Theory]
    [InlineData("Binary/1042")]
    [InlineData("https://source.example.com/fhir/Binary/1042")]
    [InlineData("urn:uuid:5b0f2c3e-1d6a-4b1e-9b4a-1f2e3d4c5b6a")]
    public void A_generic_fhir_report_keeps_embedded_content_and_drops_links_the_server_cannot_follow(string url)
    {
        var source = Json($$"""
            {"resourceType":"DiagnosticReport","status":"final","code":{"text":"CBC"},"subject":{"reference":"Patient/p1"},
             "presentedForm":[{"contentType":"application/pdf","url":"{{url}}"},{"contentType":"text/plain","data":"aGk=","url":"{{url}}"}]}
            """);

        var shaped = new GenericFhirWriteProfile("DiagnosticReport").Shape(source, Options).Resource!;

        shaped["presentedForm"]!.ToJsonString().Should().Be("""[{"contentType":"application/pdf"},{"contentType":"text/plain","data":"aGk="}]""");
    }

    [Fact]
    public void A_generic_fhir_record_whose_only_content_is_a_source_link_is_skipped()
    {
        var source = Json("""
            {"resourceType":"DiagnosticReport","status":"final","code":{"text":"CBC"},"subject":{"reference":"Patient/p1"},
             "presentedForm":[{"contentType":"application/pdf","url":"Binary/1042"}]}
            """);

        var result = new GenericFhirWriteProfile("DiagnosticReport").Shape(source, Options);

        result.Outcome.Should().Be(EhrShapeOutcome.Skipped);
        result.Reason.Should().Be("attachment-not-transferable");
    }

    // PR #244 review: a photo is not the content of a Patient; skipping the patient would leave it unmatched and
    // every dependent record unwritten.
    [Theory]
    [InlineData("Binary/1")]
    [InlineData("https://source.example.com/fhir/Binary/1")]
    public void A_generic_fhir_patient_whose_photo_is_a_source_link_loses_the_link_and_is_kept(string url)
    {
        var source = Json($$"""
            {"resourceType":"Patient","id":"p1","name":[{"family":"Doe"}],"photo":[{"contentType":"image/jpeg","url":"{{url}}"}]}
            """);

        var result = new GenericFhirWriteProfile("Patient").Shape(source, Options);

        result.Outcome.Should().Be(EhrShapeOutcome.Shaped);
        result.Resource!["photo"]!.ToJsonString().Should().Be("""[{"contentType":"image/jpeg"}]""");
    }

    [Fact]
    public void A_generic_fhir_report_with_a_conclusion_loses_its_source_linked_rendering_and_is_kept()
    {
        var source = Json("""
            {"resourceType":"DiagnosticReport","status":"final","code":{"text":"CBC"},"subject":{"reference":"Patient/p1"},
             "conclusion":"Normal","presentedForm":[{"contentType":"application/pdf","url":"Binary/1042"}]}
            """);

        var result = new GenericFhirWriteProfile("DiagnosticReport").Shape(source, Options);

        result.Outcome.Should().Be(EhrShapeOutcome.Shaped);
        result.Resource!["presentedForm"]!.ToJsonString().Should().Be("""[{"contentType":"application/pdf"}]""");
    }

    [Fact]
    public void A_generic_fhir_observation_whose_value_is_a_source_link_is_skipped()
    {
        var source = Json("""
            {"resourceType":"Observation","status":"final","code":{"text":"ECG"},"subject":{"reference":"Patient/p1"},
             "valueAttachment":{"contentType":"application/pdf","url":"Binary/9"}}
            """);

        new GenericFhirWriteProfile("Observation").Shape(source, Options).Reason.Should().Be("attachment-not-transferable");
    }

    [Fact]
    public void A_generic_fhir_note_leaves_its_first_attachment_for_the_writer_and_drops_later_source_links()
    {
        // The writer brings the first attachment's text inline from the source before the send (EhrNoteContent).
        var source = Json("""
            {"resourceType":"DocumentReference","status":"current","subject":{"reference":"Patient/p1"},
             "content":[{"attachment":{"contentType":"text/html","url":"Binary/b1"}},{"attachment":{"contentType":"application/pdf","url":"Binary/b2"}}],
             "extension":[{"url":"http://example.org/StructureDefinition/x","valueString":"kept"}]}
            """);

        var result = new GenericFhirWriteProfile("DocumentReference").Shape(source, Options);

        result.Outcome.Should().Be(EhrShapeOutcome.Shaped);
        Text(result.Resource!["content"]![0]!["attachment"]!["url"]).Should().Be("Binary/b1");
        result.Resource["content"]![1]!["attachment"]!.ToJsonString().Should().Be("""{"contentType":"application/pdf"}""");
        Text(result.Resource["extension"]![0]!["url"]).Should().Be("http://example.org/StructureDefinition/x", because: "an extension's url is not an attachment");
    }

    [Fact]
    public void A_generic_fhir_attachment_linking_a_plain_web_document_keeps_its_link()
    {
        var source = Json("""
            {"resourceType":"DiagnosticReport","status":"final","code":{"text":"CBC"},"subject":{"reference":"Patient/p1"},
             "presentedForm":[{"contentType":"application/pdf","url":"https://leaflets.example.org/cbc.pdf"}]}
            """);

        var shaped = new GenericFhirWriteProfile("DiagnosticReport").Shape(source, Options).Resource!;

        Text(shaped["presentedForm"]![0]!["url"]).Should().Be("https://leaflets.example.org/cbc.pdf");
    }

    [Fact]
    public void A_generic_fhir_create_binds_the_patient_where_the_type_keeps_it()
    {
        var shaped = new GenericFhirWriteProfile("Immunization").Shape(Json("""{"resourceType":"Immunization","status":"completed","patient":{"reference":"Patient/p1"}}"""), Options).Resource!;

        new GenericFhirWriteProfile("Immunization").BindReferences(shaped, "77", null);

        Text(shaped["patient"]!["reference"]).Should().Be("Patient/77");
    }
}

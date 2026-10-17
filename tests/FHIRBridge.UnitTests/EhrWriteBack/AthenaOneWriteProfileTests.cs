using System.Text;
using System.Text.Json.Nodes;
using FHIRBridge.Application.Abstractions.Destinations;
using FHIRBridge.Infrastructure.Destinations.EhrWriteBack;
using FHIRBridge.Infrastructure.Destinations.EhrWriteBack.Athenahealth;
using FluentAssertions;

namespace FHIRBridge.UnitTests.EhrWriteBack;

/// <summary>Each athenahealth profile describes the one athenaOne call its API reference documents.</summary>
public sealed class AthenaOneWriteProfileTests
{
    private static readonly EhrWriteBackRunOptions Options = new(true, false, 500, "preliminary", [], TargetProviderId: "71", TargetDepartmentId: "1");

    private static JsonObject Json(string json) => (JsonObject)JsonNode.Parse(json)!;

    private static string Field(EhrShapeResult result, string name) => result.Resource!["fields"]![name]!.GetValue<string>();

    [Fact]
    public void A_problem_posts_its_snomed_code_and_start_date()
    {
        var result = new AthenaOneProblemWriteProfile().Shape(Json("""
            {"resourceType":"Condition","subject":{"reference":"Patient/p"},"category":[{"coding":[{"code":"problem-list-item"}]}],
             "onsetDateTime":"2019-03-07",
             "code":{"coding":[{"system":"http://hl7.org/fhir/sid/icd-10-cm","code":"E11.9"},{"system":"http://snomed.info/sct","code":"44054006"}]}}
            """), Options);

        result.Resource!["path"]!.GetValue<string>().Should().Be("chart/{patientid}/problems");
        result.Resource["idField"]!.GetValue<string>().Should().Be("problemid");
        Field(result, "snomedcode").Should().Be("44054006");
        Field(result, "startdate").Should().Be("03/07/2019");
    }

    [Fact]
    public void A_problem_without_snomed_is_rejected_and_a_resolved_one_skipped()
    {
        var profile = new AthenaOneProblemWriteProfile();

        profile.Shape(Json("""
            {"resourceType":"Condition","subject":{"reference":"Patient/p"},"code":{"coding":[{"system":"http://hl7.org/fhir/sid/icd-10-cm","code":"E11.9"}]}}
            """), Options).Reason.Should().Be("missing-snomed-code");
        profile.Shape(Json("""
            {"resourceType":"Condition","subject":{"reference":"Patient/p"},"clinicalStatus":{"coding":[{"code":"resolved"}]},
             "code":{"coding":[{"system":"http://snomed.info/sct","code":"44054006"}]}}
            """), Options).Reason.Should().Be("not-active");
    }

    [Fact]
    public void An_allergy_looks_up_its_allergen_by_name_and_puts_it_in_the_allergies_json()
    {
        var result = new AthenaOneAllergyWriteProfile().Shape(Json("""
            {"resourceType":"AllergyIntolerance","patient":{"reference":"Patient/p"},"code":{"text":"Penicillin"},
             "reaction":[{"severity":"severe","manifestation":[{"coding":[{"system":"http://snomed.info/sct","code":"247472004","display":"Hives"}]}]}]}
            """), Options);

        var request = result.Resource!;
        request["method"]!.GetValue<string>().Should().Be("PUT");
        request["lookups"]![0]!["kind"]!.GetValue<string>().Should().Be("allergen");
        request["lookups"]![0]!["name"]!.GetValue<string>().Should().Be("Penicillin");
        var allergy = request["jsonFields"]!["allergies"]![0]!;
        allergy["allergenid"]!.GetValue<string>().Should().Be(request["lookups"]![0]!["token"]!.GetValue<string>());
        allergy["reactions"]![0]!["snomedcode"]!.GetValue<string>().Should().Be("247472004");
    }

    [Fact]
    public void Vitals_go_to_the_encounter_as_one_reading_group()
    {
        var result = new AthenaOneVitalSignWriteProfile().Shape(Json("""
            {"resourceType":"Observation","status":"final","category":[{"coding":[{"code":"vital-signs"}]}],"subject":{"reference":"Patient/p"},
             "effectiveDateTime":"2024-05-01T10:00:00Z","code":{"coding":[{"system":"http://loinc.org","code":"85354-9"}]},
             "component":[{"code":{"coding":[{"system":"http://loinc.org","code":"8480-6"}]},"valueQuantity":{"value":120}},
                          {"code":{"coding":[{"system":"http://loinc.org","code":"8462-4"}]},"valueQuantity":{"value":80}}]}
            """), Options);

        result.Resource!["path"]!.GetValue<string>().Should().Be("chart/encounter/{encounterid}/vitals");
        var group = result.Resource["jsonFields"]!["vitals"]![0]!.AsArray();
        group.Select(e => e!["clinicalelementid"]!.GetValue<string>())
            .Should().Equal("VITALS.BLOODPRESSURE.SYSTOLIC", "VITALS.BLOODPRESSURE.DIASTOLIC");
        group[0]!["value"]!.GetValue<string>().Should().Be("120");
    }

    [Fact]
    public void A_lab_observation_becomes_a_lab_result_document_with_one_analyte()
    {
        var result = new AthenaOneLabResultWriteProfile().Shape(Json("""
            {"resourceType":"Observation","status":"final","category":[{"coding":[{"code":"laboratory"}]}],"subject":{"reference":"Patient/p"},
             "effectiveDateTime":"2024-05-01T08:30:00Z","code":{"coding":[{"system":"http://loinc.org","code":"4548-4","display":"HbA1c"}]},
             "valueQuantity":{"value":7.2,"unit":"%"},"interpretation":[{"coding":[{"code":"H"}]}],
             "referenceRange":[{"low":{"value":4},"high":{"value":5.6}}]}
            """), Options);

        result.Resource!["multipart"]!.GetValue<bool>().Should().BeTrue();
        Field(result, "observationdate").Should().Be("05/01/2024");
        Field(result, "observationtime").Should().Be("08:30");
        Field(result, "providerid").Should().Be("71");
        var analyte = result.Resource["jsonFields"]!["analytes"]![0]!;
        analyte["loinc"]!.GetValue<string>().Should().Be("4548-4");
        analyte["value"]!.GetValue<string>().Should().Be("7.2");
        analyte["abnormalflag"]!.GetValue<string>().Should().Be("HIGH");
        analyte["referencerange"]!.GetValue<string>().Should().Be("4-5.6");
    }

    [Fact]
    public void A_vital_sign_is_not_a_lab_result_and_the_other_way_round()
    {
        var vital = Json("""{"resourceType":"Observation","status":"final","category":[{"coding":[{"code":"vital-signs"}]}],"subject":{"reference":"Patient/p"}}""");
        var lab = Json("""{"resourceType":"Observation","status":"final","category":[{"coding":[{"code":"laboratory"}]}],"subject":{"reference":"Patient/p"}}""");

        new AthenaOneLabResultWriteProfile().Shape(vital, Options).Reason.Should().Be("not-a-laboratory-result");
        new AthenaOneVitalSignWriteProfile().Shape(lab, Options).Reason.Should().Be("not-a-vital-sign");
    }

    [Fact]
    public void A_medication_looks_up_athenas_medication_by_name()
    {
        var result = new AthenaOneMedicationRequestWriteProfile().Shape(Json("""
            {"resourceType":"MedicationRequest","status":"active","intent":"order","subject":{"reference":"Patient/p"},
             "medicationCodeableConcept":{"text":"metformin 500 mg tablet"},
             "dispenseRequest":{"validityPeriod":{"start":"2024-01-02","end":"2024-07-02"}},
             "dosageInstruction":[{"text":"1 tab twice daily"}]}
            """), Options);

        Field(result, "medicationid").Should().Be(result.Resource!["lookups"]![0]!["token"]!.GetValue<string>());
        result.Resource["lookups"]![0]!["kind"]!.GetValue<string>().Should().Be("medication");
        Field(result, "startdate").Should().Be("01/02/2024");
        result.Resource["fields"]!.AsObject().ContainsKey("stopdate").Should().BeFalse(
            because: "an active order's validity window is when the prescription expires, not when the patient stops");
        Field(result, "unstructuredsig").Should().Be("1 tab twice daily");
    }

    // PR #244 review: athena has no medication status, so a stopped or completed medication without a stop date
    // would be listed as current.
    [Fact]
    public void A_stopped_medication_gets_a_stop_date_or_is_not_sent()
    {
        var request = new AthenaOneMedicationRequestWriteProfile();
        var statement = new AthenaOneMedicationStatementWriteProfile();

        var noDate = request.Shape(Json("""
            {"resourceType":"MedicationRequest","status":"stopped","intent":"order","subject":{"reference":"Patient/p"},
             "authoredOn":"2023-01-05","medicationCodeableConcept":{"text":"warfarin 5 mg tablet"}}
            """), Options);
        var validityEnd = request.Shape(Json("""
            {"resourceType":"MedicationRequest","status":"completed","intent":"order","subject":{"reference":"Patient/p"},
             "medicationCodeableConcept":{"text":"warfarin 5 mg tablet"},
             "dispenseRequest":{"validityPeriod":{"start":"2023-01-05","end":"2023-04-05"}}}
            """), Options);
        var statusChanged = request.Shape(Json("""
            {"resourceType":"MedicationRequest","status":"stopped","intent":"order","subject":{"reference":"Patient/p"},
             "authoredOn":"2023-01-05","statusChanged":"2023-02-10","medicationCodeableConcept":{"text":"warfarin 5 mg tablet"}}
            """), Options);
        var stoppedEarly = request.Shape(Json("""
            {"resourceType":"MedicationRequest","status":"stopped","intent":"order","subject":{"reference":"Patient/p"},
             "statusChanged":"2023-02-10","medicationCodeableConcept":{"text":"warfarin 5 mg tablet"},
             "dispenseRequest":{"validityPeriod":{"start":"2023-01-05","end":"2023-12-31"}}}
            """), Options);
        var periodEnd = statement.Shape(Json("""
            {"resourceType":"MedicationStatement","status":"completed","subject":{"reference":"Patient/p"},
             "effectivePeriod":{"start":"2023-01-05","end":"2023-03-01"},"medicationCodeableConcept":{"text":"warfarin 5 mg tablet"}}
            """), Options);

        noDate.Outcome.Should().Be(EhrShapeOutcome.Skipped);
        noDate.Reason.Should().Be("stopped-no-stop-date");
        Field(validityEnd, "stopdate").Should().Be("04/05/2023");
        Field(statusChanged, "stopdate").Should().Be("02/10/2023");
        Field(stoppedEarly, "stopdate").Should().Be("02/10/2023", because: "the validity end is when the order would have expired, not when it stopped");
        Field(periodEnd, "stopdate").Should().Be("03/01/2023");
    }

    // PR #244 re-review: R4 MedicationRequest has no statusChanged, so an order stopped early would take its future
    // validity end as the stop date and stay current in athena until then.
    [Fact]
    public void A_stopped_R4_order_with_a_future_validity_end_is_not_sent_with_that_date()
    {
        var request = new AthenaOneMedicationRequestWriteProfile();

        var futureEnd = request.Shape(Json("""
            {"resourceType":"MedicationRequest","status":"stopped","intent":"order","subject":{"reference":"Patient/p"},
             "authoredOn":"2023-01-05","medicationCodeableConcept":{"text":"warfarin 5 mg tablet"},
             "dispenseRequest":{"validityPeriod":{"start":"2023-01-05","end":"2099-12-31"}}}
            """), Options);
        var futureEndTime = request.Shape(Json("""
            {"resourceType":"MedicationRequest","status":"completed","intent":"order","subject":{"reference":"Patient/p"},
             "authoredOn":"2023-01-05","medicationCodeableConcept":{"text":"warfarin 5 mg tablet"},
             "dispenseRequest":{"validityPeriod":{"end":"2099-12-31T10:00:00Z"}}}
            """), Options);

        futureEnd.Outcome.Should().Be(EhrShapeOutcome.Skipped);
        futureEnd.Reason.Should().Be("stopped-no-stop-date");
        futureEndTime.Outcome.Should().Be(EhrShapeOutcome.Skipped);
        futureEndTime.Reason.Should().Be("stopped-no-stop-date");
    }

    // PR #244 review: a censored result keeps its comparator; a one-sided reference range is written as a bound.
    [Fact]
    public void A_lab_value_keeps_its_comparator_and_a_one_sided_range_is_a_bound()
    {
        var profile = new AthenaOneLabResultWriteProfile();
        JsonObject Lab(string value, string range) => Json($$"""
            {"resourceType":"Observation","status":"final","category":[{"coding":[{"code":"laboratory"}]}],"subject":{"reference":"Patient/p"},
             "code":{"coding":[{"system":"http://loinc.org","code":"89579-7","display":"Troponin I"}]},
             "valueQuantity":{{value}},"referenceRange":[{{range}}]}
            """);

        var below = profile.Shape(Lab("""{"comparator":"<","value":0.01,"unit":"ng/mL"}""", """{"high":{"value":0.04}}"""), Options);
        var above = profile.Shape(Lab("""{"comparator":">=","value":60}""", """{"low":{"value":60}}"""), Options);
        var ad = profile.Shape(Lab("""{"comparator":"ad","value":5}""", """{"low":{"value":1},"high":{"value":9}}"""), Options);

        var belowAnalyte = below.Resource!["jsonFields"]!["analytes"]![0]!;
        belowAnalyte["value"]!.GetValue<string>().Should().Be("<0.01");
        belowAnalyte["referencerange"]!.GetValue<string>().Should().Be("<=0.04");
        var aboveAnalyte = above.Resource!["jsonFields"]!["analytes"]![0]!;
        aboveAnalyte["value"]!.GetValue<string>().Should().Be(">=60");
        aboveAnalyte["referencerange"]!.GetValue<string>().Should().Be(">=60");
        ad.Outcome.Should().Be(EhrShapeOutcome.Skipped);
        ad.Reason.Should().Be("comparator-not-supported");
    }

    // PR #244 review: no practice time zone is configured, so a time with an offset is moved to UTC before its date
    // and time are taken (both describe the same instant).
    [Theory]
    [InlineData("2024-02-29T21:30:00-05:00", "03/01/2024", "02:30")]
    [InlineData("2024-03-01T02:30:00Z", "03/01/2024", "02:30")]
    [InlineData("2024-03-01T09:15:00+05:30", "03/01/2024", "03:45")]
    public void Lab_and_note_times_are_sent_in_utc(string effective, string date, string time)
    {
        var lab = new AthenaOneLabResultWriteProfile().Shape(Json($$$"""
            {"resourceType":"Observation","status":"final","category":[{"coding":[{"code":"laboratory"}]}],"subject":{"reference":"Patient/p"},
             "effectiveDateTime":"{{{effective}}}","code":{"coding":[{"system":"http://loinc.org","code":"4548-4","display":"HbA1c"}]},
             "valueQuantity":{"value":7.2}}
            """), Options);
        var note = new AthenaOneClinicalNoteWriteProfile().Shape(Json($$$"""
            {"resourceType":"DocumentReference","status":"current","subject":{"reference":"Patient/p"},"date":"{{{effective}}}",
             "type":{"coding":[{"system":"http://loinc.org","code":"11488-4","display":"Consult note"}]},
             "content":[{"attachment":{"contentType":"text/plain","data":"{{{Convert.ToBase64String(Encoding.UTF8.GetBytes("Seen."))}}}"}}]}
            """), Options);

        Field(lab, "observationdate").Should().Be(date);
        Field(lab, "observationtime").Should().Be(time);
        Field(note, "observationdate").Should().Be(date);
        Field(note, "observationtime").Should().Be(time);
    }

    // PR #244 review: every athena date taken from a dateTime is the UTC date (the destination's help says so), not
    // only lab and note times.
    [Fact]
    public void Onset_and_vaccine_dates_from_a_date_time_are_taken_in_utc()
    {
        var allergy = new AthenaOneAllergyWriteProfile().Shape(Json("""
            {"resourceType":"AllergyIntolerance","patient":{"reference":"Patient/p"},"code":{"text":"Penicillin"},
             "onsetDateTime":"2024-02-29T21:30:00-05:00"}
            """), Options);
        var vaccine = new AthenaOneImmunizationWriteProfile().Shape(Json("""
            {"resourceType":"Immunization","status":"completed","patient":{"reference":"Patient/p"},
             "vaccineCode":{"coding":[{"system":"http://hl7.org/fhir/sid/cvx","code":"140"}]},
             "occurrenceDateTime":"2024-02-29T21:30:00-05:00"}
            """), Options);

        allergy.Resource!["jsonFields"]!["allergies"]![0]!["onsetdate"]!.GetValue<string>().Should().Be("03/01/2024");
        Field(vaccine, "administerdate").Should().Be("03/01/2024");
    }

    [Fact]
    public void A_date_alone_is_not_moved_to_utc() =>
        AthenaOneWriteProfile.InUtc("2024-03-01").Should().Be("2024-03-01");

    // PR #244 review: the PUT upserts by allergenid, so the channel is told to read the chart first.
    [Fact]
    public void An_allergy_asks_the_channel_to_check_the_chart_for_the_allergen_first()
    {
        var result = new AthenaOneAllergyWriteProfile().Shape(Json("""
            {"resourceType":"AllergyIntolerance","patient":{"reference":"Patient/p"},"code":{"text":"Penicillin"}}
            """), Options);

        var existing = result.Resource!["existing"]!;
        existing["path"]!.GetValue<string>().Should().Be("chart/{patientid}/allergies?showinactive=true",
            because: "athena lists only active allergies otherwise, and a deactivated one would be overwritten");
        existing["list"]!.GetValue<string>().Should().Be("allergies");
        existing["match"]!.GetValue<string>().Should().Be("allergenid");
        existing["value"]!.GetValue<string>().Should().Be(result.Resource["lookups"]![0]!["token"]!.GetValue<string>());
    }

    // PR #244 review: an 'old' or 'temp' contact point is not the patient's home phone or email.
    [Fact]
    public void A_patient_old_or_temporary_phone_and_email_are_not_registered()
    {
        var shaped = new AthenaOnePatientWriteProfile().Shape(Json("""
            {"resourceType":"Patient","name":[{"family":"Doe","given":["Jo"]}],"gender":"female","birthDate":"1990-01-31",
             "telecom":[{"system":"phone","use":"old","value":"555-0100"},{"system":"phone","use":"home","value":"555-0199"},
                        {"system":"email","use":"temp","value":"old@example.org"},{"system":"email","value":"jo@example.org"},
                        {"system":"phone","value":"555-0123"}]}
            """), Options);

        Field(shaped, "homephone").Should().Be("555-0199");
        Field(shaped, "email").Should().Be("jo@example.org");
    }

    [Fact]
    public void A_vaccine_needs_a_numeric_cvx_code_and_a_full_date()
    {
        var profile = new AthenaOneImmunizationWriteProfile();
        var ok = profile.Shape(Json("""
            {"resourceType":"Immunization","status":"completed","patient":{"reference":"Patient/p"},"occurrenceDateTime":"2021-04-01T00:00:00Z",
             "vaccineCode":{"coding":[{"system":"http://hl7.org/fhir/sid/cvx","code":"208"}]}}
            """), Options);
        var monthOnly = profile.Shape(Json("""
            {"resourceType":"Immunization","status":"completed","patient":{"reference":"Patient/p"},"occurrenceDateTime":"2021-04",
             "vaccineCode":{"coding":[{"system":"http://hl7.org/fhir/sid/cvx","code":"208"}]}}
            """), Options);

        Field(ok, "cvx").Should().Be("208");
        Field(ok, "administerdate").Should().Be("04/01/2021");
        monthOnly.Reason.Should().Be("missing-occurrence-date");
    }

    [Fact]
    public void A_discharge_summary_is_an_admission_discharge_document_with_its_text()
    {
        var result = new AthenaOneClinicalNoteWriteProfile().Shape(Json($$$"""
            {"resourceType":"DocumentReference","status":"current","subject":{"reference":"Patient/p"},"date":"2024-03-05T14:20:00Z",
             "type":{"coding":[{"system":"http://loinc.org","code":"18842-5","display":"Discharge summary"}]},
             "content":[{"attachment":{"contentType":"text/plain","data":"{{{Convert.ToBase64String(Encoding.UTF8.GetBytes("Discharged home."))}}}"}}]}
            """), Options);

        Field(result, "documentsubclass").Should().Be("ADMISSIONDISCHARGE");
        Field(result, "documentdata").Should().Be("Discharged home.");
        Field(result, "observationdate").Should().Be("03/05/2024");
        result.Resource!["path"]!.GetValue<string>().Should().Be("patients/{patientid}/documents/clinicaldocument");
    }

    [Fact]
    public void A_patient_is_registered_in_the_destinations_department()
    {
        var profile = new AthenaOnePatientWriteProfile();
        var source = Json("""
            {"resourceType":"Patient","name":[{"family":"Doe","given":["Jo"]}],"gender":"female","birthDate":"1990-01-31",
             "identifier":[{"system":"http://hl7.org/fhir/sid/us-ssn","value":"123-45-6789"}],
             "telecom":[{"system":"phone","use":"mobile","value":"555-0100"}],
             "address":[{"line":["1 Main St"],"city":"Boston","state":"MA","postalCode":"02110"}]}
            """);

        var shaped = profile.Shape(source, Options);
        var noDepartment = profile.Shape(source, Options with { TargetDepartmentId = null });

        Field(shaped, "departmentid").Should().Be("1");
        Field(shaped, "firstname").Should().Be("Jo");
        Field(shaped, "dob").Should().Be("01/31/1990");
        Field(shaped, "sex").Should().Be("F");
        Field(shaped, "ssn").Should().Be("123-45-6789");
        Field(shaped, "mobilephone").Should().Be("555-0100");
        Field(shaped, "zip").Should().Be("02110");
        noDepartment.Reason.Should().Be("target-department-not-configured");
    }

    [Fact]
    public void Binding_records_the_target_patient_and_encounter_on_the_request()
    {
        var profile = new AthenaOneVitalSignWriteProfile();
        var request = new JsonObject();

        profile.BindReferences(request, "a-195900.E-12", "345");

        request["patient"]!.GetValue<string>().Should().Be("a-195900.E-12");
        request["encounter"]!.GetValue<string>().Should().Be("345");
    }
}

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
        Field(result, "stopdate").Should().Be("07/02/2024");
        Field(result, "unstructuredsig").Should().Be("1 tab twice daily");
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

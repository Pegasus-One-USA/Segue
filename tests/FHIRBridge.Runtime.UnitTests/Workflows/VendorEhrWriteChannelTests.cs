using System.Text.Json.Nodes;
using FHIRBridge.Application.Abstractions.Destinations;
using FHIRBridge.Domain.Enums;
using FHIRBridge.Runtime.Application.Abstractions.Connectors;
using FHIRBridge.Runtime.Application.DTOs;
using FHIRBridge.Runtime.Domain.Enums;
using FHIRBridge.Runtime.Infrastructure.Workflows.EhrWrite;
using FluentAssertions;
using Moq;

namespace FHIRBridge.Runtime.UnitTests.Workflows;

/// <summary>
/// The vendor write channels: eClinicalWorks wraps each create in the transaction Bundle its contracted APIs take and
/// reads its transaction-response codes; athenaOne turns a request description into one REST call, filling in the
/// department and reference-list ids first and refusing (before sending) when it cannot.
/// </summary>
public sealed class VendorEhrWriteChannelTests
{
    private const string AthenaFhirBase = "https://api.preview.platform.athenahealth.com/fhir/r4";

    private static readonly EhrWriteBackRunOptions Options = new(false, false, 500, "preliminary", []);

    private static FhirClientEhrWriteChannel Fhir(Mock<IFhirWriteClient> client, string baseUrl, SourceSystemType vendor, EhrWriteBackRunOptions? options = null, string? practiceId = null) =>
        new(
            client.Object,
            new FhirSourceConfiguration(RuntimeSourceType.Athenahealth, "write", baseUrl, "https://auth/token", "client", null, null, [], PracticeId: practiceId),
            null,
            Guid.NewGuid(),
            vendor,
            null,
            options ?? Options,
            vendorWriteApisActivated: true);

    private static FhirRawWriteResult Ok(string body) => new(FhirWriteOutcomeKind.Created, 200, body, []);

    // ---------- eClinicalWorks ----------

    [Fact]
    public void Ecw_sends_one_post_entry_typed_by_the_shaped_resource()
    {
        var bundle = JsonNode.Parse(EcwEhrWriteChannel.BuildTransaction("""{"resourceType":"MedicationStatement","status":"active"}"""))!;

        bundle["type"]!.GetValue<string>().Should().Be("transaction");
        var entry = bundle["entry"]![0]!;
        entry["request"]!["method"]!.GetValue<string>().Should().Be("POST");
        entry["request"]!["url"]!.GetValue<string>().Should().Be("MedicationStatement");
        entry["fullUrl"]!.GetValue<string>().Should().StartWith("urn:uuid:");
    }

    [Theory]
    [InlineData("1", "AllergyIntolerance/abc", EhrCreateKind.Created, "abc", null)]
    [InlineData("201 Created", "Condition/c-9/_history/1", EhrCreateKind.Created, "c-9", null)]
    [InlineData("202", null, EhrCreateKind.Rejected, null, "202")]
    [InlineData("1", null, EhrCreateKind.Unknown, null, null)]
    public void Ecw_reads_its_transaction_response(string status, string? location, EhrCreateKind kind, string? id, string? code)
    {
        var response = new JsonObject
        {
            ["resourceType"] = "Bundle",
            ["type"] = "transaction-response",
            ["entry"] = new JsonArray(new JsonObject { ["response"] = new JsonObject { ["status"] = status, ["location"] = location } }),
        };

        var outcome = EcwEhrWriteChannel.Interpret(Ok(response.ToJsonString()));

        outcome.Kind.Should().Be(kind);
        outcome.ResourceId.Should().Be(id);
        outcome.Issues.Select(i => i.VendorCode).FirstOrDefault().Should().Be(code);
    }

    [Fact]
    public void Ecw_keeps_an_http_failure_and_adds_its_own_code()
    {
        var body = """{"resourceType":"Bundle","entry":[{"response":{"status":"100"}}]}""";

        var rejected = EcwEhrWriteChannel.Interpret(new FhirRawWriteResult(FhirWriteOutcomeKind.Rejected, 400, body, []));
        var unknown = EcwEhrWriteChannel.Interpret(new FhirRawWriteResult(FhirWriteOutcomeKind.OutcomeUnknown, 500, null, []));

        rejected.Kind.Should().Be(EhrCreateKind.Rejected);
        rejected.Issues[0].VendorCode.Should().Be("100");
        unknown.Kind.Should().Be(EhrCreateKind.Unknown, because: "a 500 on a create may have filed the record");
    }

    [Fact]
    public async Task Ecw_posts_the_bundle_to_the_base_url_and_creates_a_telephone_encounter_the_same_way()
    {
        var client = new Mock<IFhirWriteClient>();
        var sent = new List<FhirRawWriteRequest>();
        client.Setup(c => c.SendAsync(Capture.In(sent), It.IsAny<FhirSourceConfiguration>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Ok("""{"resourceType":"Bundle","entry":[{"response":{"status":"1","location":"Encounter/tel-3"}}]}"""));
        var channel = new EcwEhrWriteChannel(Fhir(client, "https://staging-fhir.ecwcloud.com/fhir/r4/FFBJCD/", SourceSystemType.Healow));

        var outcome = await channel.CreateHolderEncounterAsync("ecw-p", CancellationToken.None);

        outcome.ResourceId.Should().Be("tel-3");
        var request = sent.Should().ContainSingle().Subject;
        request.Method.Should().Be("POST");
        request.Url.Should().Be("https://staging-fhir.ecwcloud.com/fhir/r4/FFBJCD");
        var encounter = JsonNode.Parse(request.Body!)!["entry"]![0]!["resource"]!;
        encounter["type"]![0]!["coding"]![0]!["code"]!.GetValue<string>().Should().Be("185317003");
        encounter["subject"]!["reference"]!.GetValue<string>().Should().Be("Patient/ecw-p");
        channel.VendorWriteApisActivated.Should().BeTrue();
    }

    // ---------- athenaOne ----------

    [Theory]
    [InlineData("a-195900.E-12345", "12345")]
    [InlineData("Patient/a-195900.E-7", "7")]
    [InlineData("88", "88")]
    [InlineData("a-195900.E-abc", null)]
    [InlineData(null, null)]
    public void Athena_patient_ids_come_from_the_fhir_enterprise_id(string? target, string? expected)
    {
        AthenaOneEhrWriteChannel.PatientId(target).Should().Be(expected);
    }

    [Theory]
    [InlineData("Patient/eA-zy.Mx3", "eA-zy.Mx3")]
    [InlineData("42", "42")]
    [InlineData("a b", null)]
    [InlineData("", null)]
    public void On_a_test_server_any_fhir_patient_id_is_used_as_it_is(string? target, string? expected)
    {
        AthenaOneEhrWriteChannel.TestServerPatientId(target).Should().Be(expected);
    }

    [Theory]
    [InlineData("195900", "https://api.preview.platform.athenahealth.com/v1/195900")]
    [InlineData("Organization/a-1.Practice-195900", "https://api.preview.platform.athenahealth.com/v1/195900")]
    public void Athena_api_base_is_the_fhir_host_with_the_practice(string practice, string expected)
    {
        AthenaOneEhrWriteChannel.ApiBase(AthenaFhirBase, practice).Should().Be(expected);
    }

    [Fact]
    public void Athena_encounters_map_open_and_review_to_in_progress()
    {
        var encounters = AthenaOneEhrWriteChannel.ToFhirEncounters("""
            {"encounters":[{"encounterid":11,"status":"OPEN","encounterdate":"05/01/2024"},
                           {"encounterid":"12","status":"REVIEW"},
                           {"encounterid":"13","status":"CLOSED"},
                           {"encounterid":"14","status":"DELETED"}]}
            """).Select(e => JsonNode.Parse(e)!).ToList();

        encounters.Select(e => (e["id"]!.GetValue<string>(), e["status"]!.GetValue<string>())).Should().Equal(
            ("11", "in-progress"), ("12", "in-progress"), ("13", "finished"), ("14", "cancelled"));
        encounters[0]["period"]!["start"]!.GetValue<string>().Should().Be("2024-05-01");
    }

    [Theory]
    [InlineData("""{"problemid":"501","success":true}""", "problemid", EhrCreateKind.Created, "501")]
    [InlineData("""[{"patientid":"9001"}]""", "patientid", EhrCreateKind.Created, "9001")]
    [InlineData("""{"vaccineids":["H77"]}""", "vaccineids", EhrCreateKind.Created, "H77")]
    [InlineData("""{"success":false,"errormessage":"x"}""", "problemid", EhrCreateKind.Rejected, null)]
    [InlineData("""{"success":"true"}""", "problemid", EhrCreateKind.Unknown, null)]
    public void Athena_reads_the_new_id_or_the_refusal(string body, string idField, EhrCreateKind kind, string? id)
    {
        var outcome = AthenaOneEhrWriteChannel.Interpret(Ok(body), idField, "op", "1", new Dictionary<string, string>());

        outcome.Kind.Should().Be(kind);
        outcome.ResourceId.Should().Be(id);
    }

    [Fact]
    public void Athena_gives_an_api_without_ids_a_stable_synthetic_one_and_reports_status_codes_without_text()
    {
        var fields = new Dictionary<string, string> { ["departmentid"] = "1", ["vitals"] = "[[]]" };

        var first = AthenaOneEhrWriteChannel.Interpret(Ok("""{"success":true}"""), null, "vitals", "12", fields);
        var second = AthenaOneEhrWriteChannel.Interpret(Ok("""{"success":true}"""), null, "vitals", "12", fields);
        var conflict = AthenaOneEhrWriteChannel.Interpret(new FhirRawWriteResult(FhirWriteOutcomeKind.Rejected, 409, """{"error":"duplicate for Jane Doe"}""", []), "x", "op", "12", fields);

        first.ResourceId.Should().StartWith("vitals:12:").And.Be(second.ResourceId);
        conflict.Issues.Should().ContainSingle().Which.VendorCode.Should().Be("athena-409");
    }

    [Fact]
    public async Task Athena_fills_in_the_department_and_lookup_and_posts_the_form()
    {
        var client = new Mock<IFhirWriteClient>();
        var sent = new List<FhirRawWriteRequest>();
        client.Setup(c => c.SendAsync(Capture.In(sent), It.IsAny<FhirSourceConfiguration>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((FhirRawWriteRequest r, FhirSourceConfiguration _, CancellationToken _) => r.Url switch
            {
                var u when u.EndsWith("/patients/12", StringComparison.Ordinal) => Ok("""[{"patientid":"12","primarydepartmentid":"150"}]"""),
                var u when u.Contains("/reference/allergies?searchvalue=Penicillin", StringComparison.Ordinal) =>
                    Ok("""[{"allergenid":"10","allergenname":"Penicillin G"},{"allergenid":"11","allergenname":"penicillin"}]"""),
                _ => Ok("""{"success":true}"""),
            });
        var channel = new AthenaOneEhrWriteChannel(Fhir(client, AthenaFhirBase, SourceSystemType.Athenahealth), "195900");
        var request = """
            {"resourceType":"AthenaOneRequest","operation":"allergy","method":"PUT","path":"chart/{patientid}/allergies",
             "multipart":false,"department":true,"fields":{},
             "jsonFields":{"allergies":[{"allergenid":"{{allergenid}}","allergenname":"Penicillin"}]},
             "lookups":[{"kind":"allergen","name":"Penicillin","token":"{{allergenid}}"}],
             "patient":"a-195900.E-12"}
            """;

        var outcome = await channel.CreateAsync("AllergyIntolerance", request, CancellationToken.None);

        outcome.Kind.Should().Be(EhrCreateKind.Created);
        sent.Select(r => r.Method).Should().Equal("GET", "GET", "PUT");
        var put = sent[^1];
        put.Url.Should().Be("https://api.preview.platform.athenahealth.com/v1/195900/chart/12/allergies");
        put.AddSourceQueryParameters.Should().BeFalse(because: "ah-practice belongs to the FHIR API, not athenaOne");
        put.Idempotent.Should().BeFalse();
        put.FormFields!["departmentid"].Should().Be("150");
        put.FormFields["allergies"].Should().Be("""[{"allergenid":"11","allergenname":"Penicillin"}]""");
    }

    [Fact]
    public async Task Athena_refuses_before_sending_when_the_lookup_has_no_exact_match()
    {
        var client = new Mock<IFhirWriteClient>();
        var sent = new List<FhirRawWriteRequest>();
        client.Setup(c => c.SendAsync(Capture.In(sent), It.IsAny<FhirSourceConfiguration>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Ok("""[{"medicationid":"1","medication":"metformin 1000 mg tablet"}]"""));
        var channel = new AthenaOneEhrWriteChannel(
            Fhir(client, AthenaFhirBase, SourceSystemType.Athenahealth, Options with { TargetDepartmentId = "1" }), "195900");
        var request = """
            {"resourceType":"AthenaOneRequest","operation":"medication","method":"POST","path":"chart/{patientid}/medications",
             "department":true,"fields":{"medicationid":"{{medicationid}}"},
             "lookups":[{"kind":"medication","name":"metformin 500 mg tablet","token":"{{medicationid}}"}],
             "patient":"a-195900.E-12"}
            """;

        var outcome = await channel.CreateAsync("MedicationRequest", request, CancellationToken.None);

        outcome.Kind.Should().Be(EhrCreateKind.Rejected);
        outcome.HttpStatus.Should().BeNull(because: "nothing was sent, so the next run may try again");
        outcome.Issues.Single().VendorCode.Should().Be("athena-medication-not-found");
        sent.Should().OnlyContain(r => r.Method == "GET");
    }

    [Fact]
    public async Task Athena_sends_a_questionnaire_response_through_fhir()
    {
        var client = new Mock<IFhirWriteClient>();
        client.Setup(c => c.CreateAsync("QuestionnaireResponse", It.IsAny<string>(), false, It.IsAny<FhirSourceConfiguration>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FhirWriteResult(FhirWriteOutcomeKind.Created, 201, "qr-1", null, null, []));
        var channel = new AthenaOneEhrWriteChannel(Fhir(client, AthenaFhirBase, SourceSystemType.Athenahealth), "195900");

        var outcome = await channel.CreateAsync("QuestionnaireResponse", """{"resourceType":"QuestionnaireResponse"}""", CancellationToken.None);

        outcome.ResourceId.Should().Be("qr-1");
        client.Verify(c => c.SendAsync(It.IsAny<FhirRawWriteRequest>(), It.IsAny<FhirSourceConfiguration>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void The_write_token_asks_for_the_proprietary_scope_once()
    {
        EhrWriteChannels.WriteScopes(["system/Patient.r"], "athena/service/Athenanet.MDP.*")
            .Should().Equal("system/Patient.r", "athena/service/Athenanet.MDP.*");
        EhrWriteChannels.WriteScopes(["athena/service/Athenanet.MDP.*"], "athena/service/Athenanet.MDP.*").Should().ContainSingle();
        EhrWriteChannels.WriteScopes(["system/Patient.r"], null).Should().Equal("system/Patient.r");
    }

    [Theory]
    [InlineData(SourceSystemType.Healow, typeof(EcwEhrWriteChannel))]
    [InlineData(SourceSystemType.Athenahealth, typeof(AthenaOneEhrWriteChannel))]
    [InlineData(SourceSystemType.Epic, typeof(FhirClientEhrWriteChannel))]
    public void Each_vendor_writes_through_its_own_channel(SourceSystemType vendor, Type expected)
    {
        var fhir = Fhir(new Mock<IFhirWriteClient>(), AthenaFhirBase, vendor, practiceId: "195900");

        EhrWriteChannels.For(vendor, fhir).Should().BeOfType(expected);
    }
}

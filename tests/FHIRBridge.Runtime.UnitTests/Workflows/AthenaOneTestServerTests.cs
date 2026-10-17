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
/// A test run as athenahealth: the athenaOne channel runs unchanged over a Generic FHIR test server, and the in-process
/// athenaOne test server answers its REST calls, filing each write on the test server where it can be inspected.
/// </summary>
public sealed class AthenaOneTestServerTests
{
    private const string TestServer = "http://localhost:8090/fhir";

    private static readonly FhirSourceConfiguration Source =
        new(RuntimeSourceType.GenericFhir, "HAPI", TestServer, null, null, null, null, []);

    private static readonly EhrWriteBackRunOptions Options =
        new(false, false, 500, "preliminary", [], TestAsVendor: SourceSystemType.Athenahealth);

    private sealed record Server(Mock<IFhirWriteClient> Client, List<(string Type, JsonObject Body)> Created, IEhrWriteChannel Channel);

    private static Server Build(string createdId = "900")
    {
        var created = new List<(string, JsonObject)>();
        var client = new Mock<IFhirWriteClient>();
        client.Setup(c => c.CreateAsync(It.IsAny<string>(), It.IsAny<string>(), false, Source, It.IsAny<CancellationToken>()))
            .Callback<string, string, bool, FhirSourceConfiguration, CancellationToken>((type, json, _, _, _) => created.Add((type, JsonNode.Parse(json)!.AsObject())))
            .ReturnsAsync(new FhirWriteResult(FhirWriteOutcomeKind.Created, 201, createdId, "1", null, []));
        client.Setup(c => c.SendAsync(It.Is<FhirRawWriteRequest>(r => r.Url == $"{TestServer}/Patient/42"), Source, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FhirRawWriteResult(FhirWriteOutcomeKind.Created, 200, """{"resourceType":"Patient","id":"42"}""", []));
        client.Setup(c => c.SearchForPatientAsync("Encounter", "42", Source, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FhirSearchPage(true, 200,
            [
                """{"resourceType":"Encounter","id":"7","status":"in-progress","period":{"start":"2026-10-01T09:00:00Z"}}""",
                """{"resourceType":"Encounter","id":"8","status":"finished"}""",
            ], []));

        var testClient = EhrWriteChannels.TestClient(SourceSystemType.Athenahealth, client.Object, Source);
        var fhir = new FhirClientEhrWriteChannel(testClient, Source, null, Guid.NewGuid(), SourceSystemType.Athenahealth, null, Options);
        return new Server(client, created, EhrWriteChannels.For(SourceSystemType.Athenahealth, fhir));
    }

    private static string Field(JsonObject basic, string name) =>
        basic["extension"]!.AsArray().OfType<JsonObject>()
            .Where(e => e["url"]!.GetValue<string>() == $"{AthenaOneTestServer.RequestSystem}:field")
            .Select(e => e["extension"]!.AsArray())
            .Single(parts => parts[0]!["valueString"]!.GetValue<string>() == name)[1]!["valueString"]!.GetValue<string>();

    [Fact]
    public async Task A_problem_is_filed_on_the_test_server_as_exactly_the_call_athenaone_would_get()
    {
        var server = Build();
        var request = """
            {"resourceType":"AthenaOneRequest","operation":"problem","method":"POST","path":"chart/{patientid}/problems",
             "multipart":false,"department":true,"fields":{"snomedcode":"38341003"},"idField":"problemid","patient":"42"}
            """;

        var outcome = await server.Channel.CreateAsync("Condition", request, CancellationToken.None);

        outcome.Kind.Should().Be(EhrCreateKind.Created);
        outcome.ResourceId.Should().Be("900");
        var (type, basic) = server.Created.Single();
        type.Should().Be("Basic");
        basic["code"]!["coding"]![0]!["code"]!.GetValue<string>().Should().Be("problems");
        basic["code"]!["coding"]![0]!["display"]!.GetValue<string>().Should().Be("POST /v1/1/chart/42/problems");
        basic["subject"]!["reference"]!.GetValue<string>().Should().Be("Patient/42");
        Field(basic, "snomedcode").Should().Be("38341003");
        Field(basic, "departmentid").Should().Be(AthenaOneTestServer.TestDepartmentId, because: "the department came from the test server's patient");
    }

    [Fact]
    public async Task A_test_server_patient_whose_id_is_not_numeric_is_still_the_patient_of_the_call()
    {
        // A shared test server also holds records copied there with their source ids (an Epic patient, say).
        var server = Build();
        server.Client.Setup(c => c.SendAsync(It.Is<FhirRawWriteRequest>(r => r.Url == $"{TestServer}/Patient/eA-zy.Mx3"), Source, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FhirRawWriteResult(FhirWriteOutcomeKind.Created, 200, """{"resourceType":"Patient","id":"eA-zy.Mx3"}""", []));
        var request = """
            {"resourceType":"AthenaOneRequest","operation":"problem","method":"POST","path":"chart/{patientid}/problems",
             "multipart":false,"department":true,"fields":{"snomedcode":"38341003"},"idField":"problemid","patient":"eA-zy.Mx3"}
            """;

        var outcome = await server.Channel.CreateAsync("Condition", request, CancellationToken.None);

        outcome.Kind.Should().Be(EhrCreateKind.Created);
        server.Created.Single().Body["subject"]!["reference"]!.GetValue<string>().Should().Be("Patient/eA-zy.Mx3");
    }

    [Fact]
    public async Task Vitals_on_an_encounter_are_filed_without_naming_a_patient()
    {
        var server = Build();
        var request = """
            {"resourceType":"AthenaOneRequest","operation":"vitals","method":"POST","path":"chart/encounter/{encounterid}/vitals",
             "multipart":false,"department":false,"fields":{},"jsonFields":{"vitals":[[{"clinicalelementid":"VITALS.WEIGHT","value":"81"}]]},
             "encounter":"7","patient":"42"}
            """;

        var outcome = await server.Channel.CreateAsync("Observation", request, CancellationToken.None);

        outcome.Kind.Should().Be(EhrCreateKind.Created);
        var basic = server.Created.Single().Body;
        basic["code"]!["coding"]![0]!["display"]!.GetValue<string>().Should().Be("POST /v1/1/chart/encounter/7/vitals");
        basic["subject"].Should().BeNull(because: "'encounter' in chart/encounter/... is not a patient id");
    }

    [Fact]
    public async Task An_allergy_lookup_finds_the_name_asked_for_and_its_id_reaches_the_call()
    {
        var server = Build();
        var request = """
            {"resourceType":"AthenaOneRequest","operation":"allergy","method":"PUT","path":"chart/{patientid}/allergies",
             "multipart":false,"department":true,"fields":{},"jsonFields":{"allergies":[{"allergenid":"{{allergenid}}"}]},
             "lookups":[{"kind":"allergen","name":"Penicillin G","token":"{{allergenid}}"}],"patient":"42"}
            """;

        var outcome = await server.Channel.CreateAsync("AllergyIntolerance", request, CancellationToken.None);

        outcome.Kind.Should().Be(EhrCreateKind.Created, because: "athena returns no id for allergies, so the channel makes a stable one");
        Field(server.Created.Single().Body, "allergies").Should().MatchRegex("""^\[\{"allergenid":"\d{6}"\}\]$""");
    }

    [Fact]
    public async Task A_new_patient_is_registered_on_the_test_server_as_a_fhir_patient()
    {
        var server = Build(createdId: "55");
        var request = """
            {"resourceType":"AthenaOneRequest","operation":"patient","method":"POST","path":"patients","multipart":false,"department":false,
             "fields":{"departmentid":"1","firstname":"Desiree","lastname":"Powell","dob":"11/14/2014","sex":"F","homephone":"6085550142","zip":"53593"},
             "idField":"patientid"}
            """;

        var outcome = await server.Channel.CreateAsync("Patient", request, CancellationToken.None);

        outcome.ResourceId.Should().Be("55");
        var (type, patient) = server.Created.Single();
        type.Should().Be("Patient");
        patient["birthDate"]!.GetValue<string>().Should().Be("2014-11-14");
        patient["gender"]!.GetValue<string>().Should().Be("female");
        patient["name"]![0]!["family"]!.GetValue<string>().Should().Be("Powell");
        patient["address"]![0]!["postalCode"]!.GetValue<string>().Should().Be("53593");
    }

    [Fact]
    public async Task Encounters_come_from_the_test_server_in_athenas_statuses()
    {
        var server = Build();

        var page = await server.Channel.SearchForPatientAsync("Encounter", "42", CancellationToken.None);

        page.Succeeded.Should().BeTrue();
        page.Resources.Select(r => JsonNode.Parse(r)!["status"]!.GetValue<string>()).Should().Equal("in-progress", "finished");
        JsonNode.Parse(page.Resources[0])!["period"]!["start"]!.GetValue<string>().Should().Be("2026-10-01");
    }

    [Fact]
    public async Task Fhir_calls_and_vendors_with_fhir_write_apis_go_to_the_test_server_as_they_are()
    {
        var client = new Mock<IFhirWriteClient>();
        client.Setup(c => c.SendAsync(It.IsAny<FhirRawWriteRequest>(), Source, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FhirRawWriteResult(FhirWriteOutcomeKind.Created, 200, "{}", []));
        var athena = EhrWriteChannels.TestClient(SourceSystemType.Athenahealth, client.Object, Source);

        await athena.SendAsync(new FhirRawWriteRequest("POST", TestServer, "{}", "application/fhir+json"), Source, CancellationToken.None);

        client.Verify(c => c.SendAsync(It.Is<FhirRawWriteRequest>(r => r.Url == TestServer), Source, It.IsAny<CancellationToken>()), Times.Once);
        EhrWriteChannels.TestClient(SourceSystemType.Healow, client.Object, Source).Should().BeSameAs(client.Object);
        EhrWriteChannels.TestClient(SourceSystemType.Epic, client.Object, Source).Should().BeSameAs(client.Object);
    }

    [Fact]
    public async Task A_call_the_test_server_does_not_know_is_refused_like_athena_would()
    {
        var server = Build();
        var request = """
            {"resourceType":"AthenaOneRequest","operation":"x","method":"GET","path":"chart/{patientid}/unknown","multipart":false,"department":false,"fields":{},"patient":"42"}
            """;

        var outcome = await server.Channel.CreateAsync("Condition", request, CancellationToken.None);

        outcome.Kind.Should().Be(EhrCreateKind.Rejected);
        outcome.Issues.Should().Contain(i => i.VendorCode == "athena-404");
    }
}

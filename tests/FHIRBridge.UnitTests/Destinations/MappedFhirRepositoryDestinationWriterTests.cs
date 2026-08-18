using System.Net;
using FHIRBridge.Application.Abstractions.Destinations;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Domain.Enums;
using FHIRBridge.Domain.ValueObjects;
using FHIRBridge.Infrastructure.Destinations;
using FluentAssertions;
using Moq;

namespace FHIRBridge.UnitTests.Destinations;

/// <summary>
/// Covers <see cref="MappedFhirRepositoryDestinationWriter"/>'s id-reconciliation behavior and its
/// <c>Coding.version</c>-stripping for stable HL7-core CodeSystems (see <c>StableCodeSystemVersions</c>) — omitting
/// version so a destination FHIR server (e.g. Aidbox) matches on <c>system</c> alone instead of rejecting a code
/// purely because its version label doesn't match whatever CodeSystem version the destination has loaded.
/// </summary>
public sealed class MappedFhirRepositoryDestinationWriterTests
{
    private static DestinationConfiguration Destination(string? target = "https://fhir.example.com") =>
        new("FHIR Store", DestinationType.FhirRepository, new SecretReference("kv", "secret"), target);

    private static MappingProfile Mapping() =>
        new("Patient FHIR", "Patient", Guid.NewGuid(), Guid.NewGuid(), "Patient", []);

    private static MappedDestinationRecord Record(string? sourceJson, string sourceResourceId = "123", string resourceType = "Patient") =>
        new(Guid.NewGuid(), resourceType, resourceType, sourceResourceId, new Dictionary<string, object?>(), sourceJson);

    private static PipelineWriteContext Context() => new(true, "Workflow", DateTimeOffset.UtcNow);

    private static (MappedFhirRepositoryDestinationWriter Writer, CapturingHandler Handler) CreateWriter(
        string secretValue = "https://fhir.example.com")
    {
        var handler = new CapturingHandler();
        var httpClientFactory = new Mock<IHttpClientFactory>();
        httpClientFactory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(new HttpClient(handler));

        var secretProvider = new Mock<ISecretProvider>();
        secretProvider
            .Setup(s => s.GetSecretAsync(It.IsAny<SecretReference>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(secretValue);

        var writer = new MappedFhirRepositoryDestinationWriter(secretProvider.Object, httpClientFactory.Object);
        return (writer, handler);
    }

    [Fact]
    public async Task Source_json_is_used_verbatim_with_id_reconciled()
    {
        var (writer, handler) = CreateWriter();
        var record = Record("""{"resourceType":"Patient","name":[{"family":"Doe"}]}""");

        await writer.WriteAsync(Destination(), Mapping(), [record], Context(), CancellationToken.None);

        handler.LastRequestBody.Should().Contain("\"resourceType\":\"Patient\"");
        handler.LastRequestBody.Should().Contain("\"id\":\"123\"");
    }

    [Fact]
    public async Task Version_is_stripped_from_a_coding_on_a_stable_catalog_CodeSystem()
    {
        var (writer, handler) = CreateWriter();
        var record = Record(
            """
            {"resourceType":"Condition","id":"c1","clinicalStatus":{"coding":[
                {"system":"http://terminology.hl7.org/CodeSystem/condition-clinical","version":"4.0.0","code":"active"}
            ]}}
            """,
            resourceType: "Condition");

        await writer.WriteAsync(Destination(), Mapping(), [record], Context(), CancellationToken.None);

        handler.LastRequestBody.Should().Contain("\"code\":\"active\"");
        handler.LastRequestBody.Should().NotContain("\"version\"");
    }

    [Fact]
    public async Task Version_is_stripped_from_condition_verificationStatus_and_category()
    {
        var (writer, handler) = CreateWriter();
        var record = Record(
            """
            {"resourceType":"Condition","id":"c1",
             "verificationStatus":{"coding":[{"system":"http://terminology.hl7.org/CodeSystem/condition-ver-status","version":"4.0.0","code":"confirmed"}]},
             "category":[{"coding":[{"system":"http://terminology.hl7.org/CodeSystem/condition-category","version":"4.0.0","code":"encounter-diagnosis"}]}]}
            """,
            resourceType: "Condition");

        await writer.WriteAsync(Destination(), Mapping(), [record], Context(), CancellationToken.None);

        handler.LastRequestBody.Should().NotContain("\"version\"");
        handler.LastRequestBody.Should().Contain("\"code\":\"confirmed\"");
        handler.LastRequestBody.Should().Contain("\"code\":\"encounter-diagnosis\"");
    }

    [Fact]
    public async Task Version_is_stripped_from_allergyintolerance_verification_an_additive_only_R5_change()
    {
        // R5 adds a 5th code ("presumed") but removes nothing, so a source that only emits R4-era codes (Epic) can
        // never be invalidated by this change — this is the real-world shape that failed: Epic tagged "4.0.0" while
        // the destination had "4.0.1" loaded, a benign within-R4 version bump on an identical code set.
        var (writer, handler) = CreateWriter();
        var record = Record(
            """
            {"resourceType":"AllergyIntolerance","id":"a1","verificationStatus":{"coding":[
                {"system":"http://terminology.hl7.org/CodeSystem/allergyintolerance-verification","version":"4.0.0","code":"confirmed"}
            ]}}
            """,
            resourceType: "AllergyIntolerance");

        await writer.WriteAsync(Destination(), Mapping(), [record], Context(), CancellationToken.None);

        handler.LastRequestBody.Should().Contain("\"code\":\"confirmed\"");
        handler.LastRequestBody.Should().NotContain("\"version\"");
    }

    [Fact]
    public async Task Version_is_stripped_from_composition_status_whose_R5_hierarchy_kept_all_original_codes()
    {
        var (writer, handler) = CreateWriter();
        var record = Record(
            """
            {"resourceType":"DocumentReference","id":"d1","docStatus":"final","category":[{"coding":[
                {"system":"http://hl7.org/fhir/composition-status","version":"4.0.1","code":"final"}
            ]}]}
            """,
            resourceType: "DocumentReference");

        await writer.WriteAsync(Destination(), Mapping(), [record], Context(), CancellationToken.None);

        handler.LastRequestBody.Should().Contain("\"code\":\"final\"");
        handler.LastRequestBody.Should().NotContain("\"version\"");
    }

    [Fact]
    public async Task Version_is_preserved_on_encounter_status_a_CodeSystem_with_real_cross_version_code_drift()
    {
        var (writer, handler) = CreateWriter();
        var record = Record(
            """
            {"resourceType":"Encounter","id":"e1","status":"finished",
             "statusHistory":[{"status":"onleave","coding":[{"system":"http://hl7.org/fhir/encounter-status","version":"4.0.0","code":"onleave"}]}]}
            """,
            resourceType: "Encounter");

        await writer.WriteAsync(Destination(), Mapping(), [record], Context(), CancellationToken.None);

        handler.LastRequestBody.Should().Contain("\"version\":\"4.0.0\"");
    }

    [Fact]
    public async Task Version_is_preserved_on_a_non_catalog_CodeSystem_such_as_SNOMED_CT()
    {
        var (writer, handler) = CreateWriter();
        var record = Record(
            """{"resourceType":"Condition","id":"c1","code":{"coding":[{"system":"http://snomed.info/sct","version":"http://snomed.info/sct/731000124108","code":"38341003"}]}}""",
            resourceType: "Condition");

        await writer.WriteAsync(Destination(), Mapping(), [record], Context(), CancellationToken.None);

        handler.LastRequestBody.Should().Contain("\"version\":\"http://snomed.info/sct/731000124108\"");
    }

    [Fact]
    public async Task Resource_with_no_coding_anywhere_is_unaffected_by_version_stripping()
    {
        var (writer, handler) = CreateWriter();
        var record = Record("""{"resourceType":"Patient","id":"p1","name":[{"family":"Doe"}]}""");

        await writer.WriteAsync(Destination(), Mapping(), [record], Context(), CancellationToken.None);

        handler.LastRequestBody.Should().Contain("\"family\":\"Doe\"");
    }

    [Fact]
    public async Task Non_FHIR_fallback_record_is_posted_as_the_flattened_payload()
    {
        var (writer, handler) = CreateWriter();
        var record = Record(sourceJson: null, sourceResourceId: "fallback1", resourceType: "Custom");

        await writer.WriteAsync(Destination(), Mapping(), [record], Context(), CancellationToken.None);

        handler.LastRequest!.RequestUri!.ToString().Should().Contain("Custom/fallback1");
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastRequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            LastRequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}

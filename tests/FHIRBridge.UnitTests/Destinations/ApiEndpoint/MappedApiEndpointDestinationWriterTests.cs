using System.Text.Json;
using FHIRBridge.Application.Abstractions.Destinations;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Domain.Enums;
using FHIRBridge.Domain.ValueObjects;
using FHIRBridge.Infrastructure.Destinations;
using FHIRBridge.Infrastructure.Destinations.ApiEndpoint;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace FHIRBridge.UnitTests.Destinations.ApiEndpoint;

/// <summary>
/// Exercises the writer against a stubbed <see cref="IApiEndpointSender"/> — the seam that exists precisely so
/// batching, framing and failure handling can be asserted without a live HTTP endpoint.
/// </summary>
public sealed class MappedApiEndpointDestinationWriterTests
{
    private readonly Mock<IApiEndpointSender> _sender = new();
    private readonly List<ApiEndpointBatch> _sent = [];

    private static readonly Guid RunId = Guid.Parse("11111111-2222-3333-4444-555555555555");

    private static DestinationConfiguration Destination(string? connectionMetadataJson = null) =>
        new(
            "Partner API",
            DestinationType.ApiEndpoint,
            new SecretReference("kv", "secret"),
            "https://api.example.com/records",
            connectionMetadataJson);

    private static MappingProfile Mapping(string resourceType = "Patient", string destinationObject = "Patient") =>
        new("Api Mapping", resourceType, Guid.NewGuid(), Guid.NewGuid(), destinationObject, []);

    private static MappedDestinationRecord Record(string id) =>
        new(RunId, "Patient", "Patient", id, new Dictionary<string, object?> { ["Name"] = "Alice" }, null);

    private static MappedDestinationRecord RecordWithValues(string id, Dictionary<string, object?> values) =>
        new(RunId, "Patient", "Patient", id, values, null);

    private static PipelineWriteContext Context() => new(false, "Partner Export Workflow", DateTimeOffset.UtcNow, "corr-1");

    private MappedApiEndpointDestinationWriter CreateWriter() =>
        new(_sender.Object, new ApiEndpointMultiResourceAccumulator(), NullLogger<MappedApiEndpointDestinationWriter>.Instance);

    private void SetupSender(bool delivered = true, string? error = null)
        => _sender
            .Setup(s => s.SendAsync(
                It.IsAny<DestinationConfiguration>(),
                It.IsAny<ApiEndpointSettings>(),
                It.IsAny<ApiEndpointBatch>(),
                It.IsAny<CancellationToken>()))
            .Callback<DestinationConfiguration, ApiEndpointSettings, ApiEndpointBatch, CancellationToken>(
                (_, _, batch, _) => _sent.Add(batch))
            .ReturnsAsync(new ApiEndpointSendResult(delivered, delivered ? 202 : 500, 1, error));

    [Fact]
    public async Task An_empty_batch_sends_nothing_at_all()
    {
        SetupSender();

        var result = await CreateWriter().WriteAsync(Destination(), Mapping(), [], Context(), CancellationToken.None);

        result.Count.Should().Be(0);
        _sent.Should().BeEmpty();
    }

    [Fact]
    public async Task Records_are_framed_as_a_single_json_array_by_default()
    {
        SetupSender();

        var result = await CreateWriter().WriteAsync(
            Destination(), Mapping(), [Record("p1"), Record("p2"), Record("p3")], Context(), CancellationToken.None);

        result.Count.Should().Be(3);
        _sent.Should().HaveCount(1);
        _sent[0].Body.Should().StartWith("[").And.EndWith("]");
        _sent[0].RecordCount.Should().Be(3);
    }

    [Fact]
    public async Task The_batch_is_split_when_the_configured_record_count_is_reached()
    {
        SetupSender();
        var records = Enumerable.Range(1, 5).Select(i => Record($"p{i}")).ToArray();

        var result = await CreateWriter().WriteAsync(
            Destination("""{"dest_apiBatchSize":"2"}"""), Mapping(), records, Context(), CancellationToken.None);

        result.Count.Should().Be(5);
        _sent.Select(batch => batch.RecordCount).Should().BeEquivalentTo([2, 2, 1], options => options.WithStrictOrdering());
    }

    [Fact]
    public async Task RecordPerRequest_sends_one_request_per_record_regardless_of_batch_size()
    {
        SetupSender();

        var result = await CreateWriter().WriteAsync(
            Destination("""{"dest_apiPayloadShape":"recordPerRequest"}"""),
            Mapping(),
            [Record("p1"), Record("p2")],
            Context(),
            CancellationToken.None);

        result.Count.Should().Be(2);
        _sent.Should().HaveCount(2);
        _sent.Should().OnlyContain(batch => batch.RecordCount == 1);
    }

    [Fact]
    public async Task A_failed_batch_throws_by_default_so_data_loss_is_never_silent()
    {
        SetupSender(delivered: false, error: "HTTP 500 Internal Server Error");

        var act = () => CreateWriter().WriteAsync(
            Destination(), Mapping(), [Record("p1")], Context(), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*not delivered*");
    }

    [Fact]
    public async Task A_body_template_substitutes_mapped_values_with_their_real_types_preserved()
    {
        SetupSender();
        var record = RecordWithValues("p1", new Dictionary<string, object?> { ["Age"] = 42, ["Name"] = "Alice" });
        var template = """{"patient":{"fullName":"{{Name}}","age":"{{Age}}","externalId":"{{sourceResourceId}}"}}""";
        var connectionMetadataJson = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["dest_apiBodyTemplateJson"] = template,
        });

        await CreateWriter().WriteAsync(
            Destination(connectionMetadataJson),
            Mapping(),
            [record],
            Context(),
            CancellationToken.None);

        _sent.Should().HaveCount(1);
        using var body = JsonDocument.Parse(_sent[0].Body);
        var patient = body.RootElement[0].GetProperty("patient");
        patient.GetProperty("fullName").GetString().Should().Be("Alice");
        patient.GetProperty("age").GetInt32().Should().Be(42, "a template value that is exactly one placeholder keeps its real JSON type, not a stringified \"42\"");
        patient.GetProperty("externalId").GetString().Should().Be("p1", "sourceResourceId is addressable in a template alongside mapped field names");
    }

    [Fact]
    public async Task A_body_template_interpolates_a_placeholder_embedded_in_a_larger_string()
    {
        SetupSender();
        var record = RecordWithValues("p1", new Dictionary<string, object?> { ["Age"] = 42 });
        var template = """{"summary":"Patient is {{Age}} years old"}""";
        var connectionMetadataJson = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["dest_apiBodyTemplateJson"] = template,
        });

        await CreateWriter().WriteAsync(
            Destination(connectionMetadataJson),
            Mapping(),
            [record],
            Context(),
            CancellationToken.None);

        using var body = JsonDocument.Parse(_sent[0].Body);
        body.RootElement[0].GetProperty("summary").GetString().Should().Be("Patient is 42 years old");
    }

    [Fact]
    public async Task IsolateBatch_reports_the_failure_as_a_RecordError_instead_of_throwing()
    {
        SetupSender(delivered: false, error: "HTTP 500 Internal Server Error");

        var result = await CreateWriter().WriteAsync(
            Destination("""{"dest_apiOnFailure":"isolateBatch"}"""),
            Mapping(),
            [Record("p1")],
            Context(),
            CancellationToken.None);

        result.Count.Should().Be(0);
        result.RecordErrors.Should().ContainSingle().Which.Should().Contain("not delivered");
    }

    [Fact]
    public async Task Multi_resource_flat_mode_waits_for_every_resource_type_then_sends_one_combined_document()
    {
        SetupSender();
        var destination = Destination(JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["dest_apiMultiResourceMode"] = "flat",
            ["dest_apiResourceRelationsJson"] = """
                [
                    {"resourceType":"Patient","nestKey":"patients"},
                    {"resourceType":"Encounter","nestKey":"encounters"}
                ]
                """,
        }));
        var writer = CreateWriter();

        var patientResult = await writer.WriteAsync(
            destination, Mapping("Patient", "Patient"), [Record("p1")], Context(), CancellationToken.None);

        patientResult.Count.Should().Be(1, "the first resource type's records are accepted but nothing is sent until every resource type lands");
        _sent.Should().BeEmpty();

        var encounterResult = await writer.WriteAsync(
            destination, Mapping("Encounter", "Encounter"), [Record("e1")], Context(), CancellationToken.None);

        encounterResult.Count.Should().Be(1);
        _sent.Should().HaveCount(1, "the last resource type to land triggers exactly one combined send");
        using var body = JsonDocument.Parse(_sent[0].Body);
        body.RootElement.GetProperty("patients").GetArrayLength().Should().Be(1);
        body.RootElement.GetProperty("encounters").GetArrayLength().Should().Be(1);
    }

    [Fact]
    public async Task Multi_resource_nested_mode_correlates_children_under_their_parent_record()
    {
        SetupSender();
        var destination = Destination(JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["dest_apiMultiResourceMode"] = "nested",
            ["dest_apiResourceRelationsJson"] = """
                [
                    {"resourceType":"Patient","nestKey":"patients"},
                    {"resourceType":"Encounter","parentResourceType":"Patient","correlationColumn":"PatientId","parentKeyColumn":"Id","nestKey":"encounters"}
                ]
                """,
        }));
        var writer = CreateWriter();

        await writer.WriteAsync(
            destination,
            Mapping("Patient", "Patient"),
            [RecordWithValues("p1", new Dictionary<string, object?> { ["Id"] = "pat-1" })],
            Context(),
            CancellationToken.None);
        await writer.WriteAsync(
            destination,
            Mapping("Encounter", "Encounter"),
            [RecordWithValues("e1", new Dictionary<string, object?> { ["PatientId"] = "pat-1" })],
            Context(),
            CancellationToken.None);

        _sent.Should().HaveCount(1);
        using var body = JsonDocument.Parse(_sent[0].Body);
        var patients = body.RootElement.GetProperty("patients");
        patients.GetArrayLength().Should().Be(1);
        var encounters = patients[0].GetProperty("encounters");
        encounters.GetArrayLength().Should().Be(1, "the encounter record is nested under the patient it correlates to, not left top-level");
        body.RootElement.TryGetProperty("encounters", out _).Should().BeFalse("a non-root resource type is nested only, never also emitted top-level");
    }
}

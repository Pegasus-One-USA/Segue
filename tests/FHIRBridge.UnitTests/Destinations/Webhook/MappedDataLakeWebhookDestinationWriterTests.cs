using System.Text.Json;
using FHIRBridge.Application.Abstractions.Destinations;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Domain.Enums;
using FHIRBridge.Domain.ValueObjects;
using FHIRBridge.Infrastructure.Destinations;
using FHIRBridge.Infrastructure.Destinations.Webhook;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace FHIRBridge.UnitTests.Destinations.Webhook;

/// <summary>
/// Exercises the writer against a stubbed <see cref="IDataLakeWebhookSender"/> — the seam that exists precisely so
/// batching, framing and failure handling can be asserted without a live ingestion endpoint.
/// </summary>
public sealed class MappedDataLakeWebhookDestinationWriterTests
{
    private readonly Mock<IDataLakeWebhookSender> _sender = new();
    private readonly List<DataLakeWebhookBatch> _sent = [];

    private static readonly Guid RunId = Guid.Parse("11111111-2222-3333-4444-555555555555");

    private static DestinationConfiguration Destination(string? connectionMetadataJson = null) =>
        new(
            "Lake Feed",
            DestinationType.DataLakeWebhook,
            new SecretReference("kv", "secret"),
            "https://ingest.example.com/events",
            connectionMetadataJson);

    private static MappingProfile Mapping(string resourceType = "Patient", string destinationObject = "Patient") =>
        new("Lake Mapping", resourceType, Guid.NewGuid(), Guid.NewGuid(), destinationObject, []);

    private static MappedDestinationRecord Record(string id, string? sourceJson = null) =>
        new(RunId, "Patient", "Patient", id, new Dictionary<string, object?> { ["Name"] = "Alice" }, sourceJson);

    private static PipelineWriteContext Context() => new(false, "Lake Export Workflow", DateTimeOffset.UtcNow, "corr-1");

    private MappedDataLakeWebhookDestinationWriter CreateWriter() =>
        new(_sender.Object, NullLogger<MappedDataLakeWebhookDestinationWriter>.Instance);

    private void SetupSender(bool delivered = true, string? error = null)
        => _sender
            .Setup(s => s.SendAsync(
                It.IsAny<DestinationConfiguration>(),
                It.IsAny<DataLakeWebhookSettings>(),
                It.IsAny<DataLakeWebhookBatch>(),
                It.IsAny<CancellationToken>()))
            .Callback<DestinationConfiguration, DataLakeWebhookSettings, DataLakeWebhookBatch, CancellationToken>(
                (_, _, batch, _) => _sent.Add(batch))
            .ReturnsAsync(new DataLakeWebhookSendResult(delivered, delivered ? 202 : 500, 1, error));

    [Fact]
    public async Task An_empty_batch_sends_nothing_at_all()
    {
        SetupSender();

        var result = await CreateWriter().WriteAsync(
            Destination(), Mapping(), [], Context(), CancellationToken.None);

        result.Count.Should().Be(0);
        _sent.Should().BeEmpty();
    }

    [Fact]
    public async Task Records_are_framed_as_one_ndjson_line_each_by_default()
    {
        SetupSender();

        var result = await CreateWriter().WriteAsync(
            Destination(), Mapping(), [Record("p1"), Record("p2"), Record("p3")], Context(), CancellationToken.None);

        result.Count.Should().Be(3);
        _sent.Should().HaveCount(1);
        _sent[0].Body.Split('\n').Should().HaveCount(3);
        _sent[0].Body.Should().NotContain("\r", "CRLF is what makes Windows-produced NDJSON fail on a Spark reader");
        _sent[0].RecordCount.Should().Be(3);
    }

    [Fact]
    public async Task The_batch_is_split_when_the_configured_record_count_is_reached()
    {
        SetupSender();
        var records = Enumerable.Range(1, 5).Select(i => Record($"p{i}")).ToArray();

        var result = await CreateWriter().WriteAsync(
            Destination("""{"dest_dlwBatchSize":"2"}"""), Mapping(), records, Context(), CancellationToken.None);

        result.Count.Should().Be(5);
        _sent.Select(batch => batch.RecordCount).Should().BeEquivalentTo([2, 2, 1], options => options.WithStrictOrdering());
    }

    [Fact]
    public async Task The_batch_is_also_split_on_the_byte_ceiling_even_when_the_record_count_allows_more()
    {
        SetupSender();

        // MaxRequestBytes is clamped to a 1024-byte floor, so each record is padded past ~512 bytes to make the
        // byte ceiling — rather than the record count — the binding constraint.
        var padding = new string('x', 700);
        var records = Enumerable.Range(1, 4)
            .Select(i => new MappedDestinationRecord(
                RunId, "Patient", "Patient", $"p{i}", new Dictionary<string, object?> { ["Note"] = padding }))
            .ToArray();

        // A ceiling below two serialized records forces one record per request despite a batch size of 500.
        var result = await CreateWriter().WriteAsync(
            Destination("""{"dest_dlwMaxRequestBytes":"1024","dest_dlwBatchSize":"500"}"""),
            Mapping(),
            records,
            Context(),
            CancellationToken.None);

        result.Count.Should().Be(4);
        _sent.Should().HaveCount(4);
        _sent.Should().OnlyContain(batch => batch.RecordCount == 1);
    }

    [Fact]
    public async Task A_single_record_larger_than_the_ceiling_is_still_sent_rather_than_dropped()
    {
        SetupSender();
        var oversized = Record("p1", sourceJson: $$"""{"id":"p1","note":"{{new string('x', 4000)}}"}""");

        var result = await CreateWriter().WriteAsync(
            Destination("""{"dest_dlwMaxRequestBytes":"1024","dest_dlwIncludeSourceJson":"true"}"""),
            Mapping(),
            [oversized],
            Context(),
            CancellationToken.None);

        result.Count.Should().Be(1);
        _sent.Should().HaveCount(1);
    }

    [Fact]
    public async Task JsonArray_shape_produces_one_array_document()
    {
        SetupSender();

        await CreateWriter().WriteAsync(
            Destination("""{"dest_dlwPayloadShape":"jsonArray"}"""),
            Mapping(),
            [Record("p1"), Record("p2")],
            Context(),
            CancellationToken.None);

        var parsed = JsonDocument.Parse(_sent[0].Body);
        parsed.RootElement.ValueKind.Should().Be(JsonValueKind.Array);
        parsed.RootElement.GetArrayLength().Should().Be(2);
    }

    [Fact]
    public async Task Envelope_shape_carries_run_provenance_alongside_the_records()
    {
        SetupSender();

        await CreateWriter().WriteAsync(
            Destination("""{"dest_dlwPayloadShape":"envelope"}"""),
            Mapping(),
            [Record("p1")],
            Context(),
            CancellationToken.None);

        var root = JsonDocument.Parse(_sent[0].Body).RootElement;
        root.GetProperty("meta").GetProperty("resourceType").GetString().Should().Be("Patient");
        root.GetProperty("meta").GetProperty("routeName").GetString().Should().Be("Lake Export Workflow");
        root.GetProperty("meta").GetProperty("correlationId").GetString().Should().Be("corr-1");
        root.GetProperty("records").GetArrayLength().Should().Be(1);
    }

    [Fact]
    public async Task RecordPerRequest_shape_sends_one_request_per_record_and_ignores_batching()
    {
        SetupSender();

        await CreateWriter().WriteAsync(
            Destination("""{"dest_dlwPayloadShape":"recordPerRequest","dest_dlwBatchSize":"500"}"""),
            Mapping(),
            [Record("p1"), Record("p2")],
            Context(),
            CancellationToken.None);

        _sent.Should().HaveCount(2);
        _sent.Should().OnlyContain(batch => batch.RecordCount == 1);
        JsonDocument.Parse(_sent[0].Body).RootElement.ValueKind.Should().Be(JsonValueKind.Object);
    }

    [Fact]
    public async Task The_source_resource_is_omitted_unless_explicitly_opted_into()
    {
        SetupSender();
        var record = Record("p1", sourceJson: """{"resourceType":"Patient","id":"p1"}""");

        await CreateWriter().WriteAsync(Destination(), Mapping(), [record], Context(), CancellationToken.None);

        // Asserting on the absence of the "resource" property specifically: the payload legitimately carries its
        // own top-level "resourceType" field, so a substring check for that would match either way.
        JsonDocument.Parse(_sent[0].Body).RootElement.TryGetProperty("resource", out _)
            .Should().BeFalse("dest_dlwIncludeSourceJson defaults to off");
    }

    [Fact]
    public async Task Opting_in_embeds_the_source_resource_as_a_real_nested_object()
    {
        SetupSender();
        var record = Record("p1", sourceJson: """{"resourceType":"Patient","id":"p1"}""");

        await CreateWriter().WriteAsync(
            Destination("""{"dest_dlwIncludeSourceJson":"true"}"""),
            Mapping(),
            [record],
            Context(),
            CancellationToken.None);

        var resource = JsonDocument.Parse(_sent[0].Body).RootElement.GetProperty("resource");
        resource.ValueKind.Should().Be(JsonValueKind.Object, "a JSON string would force the consumer to double-decode");
        resource.GetProperty("id").GetString().Should().Be("p1");
    }

    [Fact]
    public async Task The_idempotency_key_is_deterministic_so_a_re_driven_run_can_be_de_duplicated_downstream()
    {
        SetupSender();
        var records = Enumerable.Range(1, 4).Select(i => Record($"p{i}")).ToArray();
        var writer = CreateWriter();
        var destination = Destination("""{"dest_dlwBatchSize":"2"}""");

        await writer.WriteAsync(destination, Mapping(), records, Context(), CancellationToken.None);
        var firstRun = _sent.Select(batch => batch.IdempotencyKey).ToArray();

        _sent.Clear();
        await writer.WriteAsync(destination, Mapping(), records, Context(), CancellationToken.None);

        _sent.Select(batch => batch.IdempotencyKey).Should().BeEquivalentTo(firstRun, options => options.WithStrictOrdering());
        firstRun.Should().OnlyHaveUniqueItems("each batch within a run must be distinguishable");
        firstRun[0].Should().Be($"{RunId:N}:patient:patient:0");
    }

    [Fact]
    public async Task A_failed_batch_fails_the_whole_write_by_default()
    {
        SetupSender(delivered: false, error: "HTTP 500 Internal Server Error");

        var act = () => CreateWriter().WriteAsync(
            Destination(), Mapping(), [Record("p1")], Context(), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Data-lake webhook delivery*failed*");
    }

    [Fact]
    public async Task IsolateBatch_reports_the_failure_through_RecordErrors_and_keeps_the_delivered_count_honest()
    {
        var responses = new Queue<bool>([true, false, true]);
        _sender
            .Setup(s => s.SendAsync(
                It.IsAny<DestinationConfiguration>(),
                It.IsAny<DataLakeWebhookSettings>(),
                It.IsAny<DataLakeWebhookBatch>(),
                It.IsAny<CancellationToken>()))
            .Callback<DestinationConfiguration, DataLakeWebhookSettings, DataLakeWebhookBatch, CancellationToken>(
                (_, _, batch, _) => _sent.Add(batch))
            .ReturnsAsync(() =>
            {
                var delivered = responses.Dequeue();
                return new DataLakeWebhookSendResult(delivered, delivered ? 202 : 503, 4, delivered ? null : "HTTP 503");
            });

        var records = Enumerable.Range(1, 6).Select(i => Record($"p{i}")).ToArray();

        var result = await CreateWriter().WriteAsync(
            Destination("""{"dest_dlwBatchSize":"2","dest_dlwOnFailure":"isolateBatch"}"""),
            Mapping(),
            records,
            Context(),
            CancellationToken.None);

        result.Count.Should().Be(4, "only the two delivered batches count as written");
        result.RecordErrors.Should().HaveCount(1);
        result.RecordErrors![0].Should().Contain("Batch 2/3").And.Contain("HTTP 503");
        result.WrittenResourceIds.Should().BeEquivalentTo(["p1", "p2", "p5", "p6"]);
    }
}

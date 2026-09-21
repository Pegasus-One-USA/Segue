using FHIRBridge.Application.DTOs;
using FHIRBridge.Application.Services;
using FHIRBridge.Runtime.Application.Workflows;
using FHIRBridge.Runtime.Application.Workflows.Catalog;
using FHIRBridge.Runtime.Domain.Workflows;
using FHIRBridge.Runtime.Infrastructure.Workflows.Executors;
using FluentAssertions;
using Moq;
using Xunit;

namespace FHIRBridge.Runtime.UnitTests.Workflows;

/// <summary>
/// Proves dual-read end to end: the same node configuration, written flat or enveloped, must reach an executor
/// identically. Unit-testing <see cref="WorkflowNodeConfigurationEnvelope"/> alone would not catch a reader that
/// parses config itself instead of going through the shared helpers — this goes through a real executor.
/// See docs/backend/18-workflow-self-contained-config-plan.md §3.
/// </summary>
public sealed class EnvelopedNodeConfigurationReadTests
{
    private const string MeasureId = "CMS122-HbA1c";

    private static WorkflowNode NodeWith(string configurationJson) =>
        new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            WorkflowNodeTypes.HedisMeasureReport,
            WorkflowNodeCategory.Analytics,
            rank: 80,
            subRank: 0,
            displayName: "HEDIS",
            configurationJson,
            positionX: 0,
            positionY: 0,
            isEnabled: true);

    /// <summary>Returns the measureId the executor actually resolved, by capturing what it asked the service for.</summary>
    private static async Task<string?> ResolveMeasureIdAsync(string configurationJson)
    {
        string? observed = null;
        var service = new Mock<IHedisMeasureReportService>();
        service
            .Setup(s => s.GenerateAsync(
                It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .Callback<string, DateTime, DateTime, CancellationToken>((measureId, _, _, _) => observed = measureId)
            .ReturnsAsync(new HedisMeasureReportDto(
                "MeasureReport",
                "report-1",
                "complete",
                "summary",
                "CMS122",
                new HedisMeasurePeriodDto(DateTime.UtcNow.AddDays(-30), DateTime.UtcNow),
                []));

        var executor = new HedisMeasureReportNodeExecutor(service.Object);
        await executor.ExecuteAsync(
            new WorkflowExecutionContext(Guid.NewGuid(), "corr-1"),
            NodeWith(configurationJson),
            [],
            CancellationToken.None);

        return observed;
    }

    [Fact]
    public async Task A_legacy_flat_node_resolves_its_configuration()
    {
        var measureId = await ResolveMeasureIdAsync($$"""{ "measureId": "{{MeasureId}}" }""");

        measureId.Should().Be(MeasureId);
    }

    [Fact]
    public async Task An_enveloped_node_resolves_the_same_configuration()
    {
        var measureId = await ResolveMeasureIdAsync(
            $$"""{ "ref": { "masterId": "m1", "masterVersion": 3 }, "config": { "measureId": "{{MeasureId}}" } }""");

        measureId.Should().Be(MeasureId);
    }

    /// <summary>Both shapes must be readable at once — that is the whole point of dual-read, and what lets the
    /// migration be run and verified without a flag day.</summary>
    [Fact]
    public async Task Both_shapes_resolve_identically()
    {
        var flat = await ResolveMeasureIdAsync($$"""{ "measureId": "{{MeasureId}}" }""");
        var enveloped = await ResolveMeasureIdAsync(
            $$"""{ "ref": { "masterId": "m1" }, "config": { "measureId": "{{MeasureId}}" } }""");

        enveloped.Should().Be(flat);
    }

    [Fact]
    public async Task An_enveloped_node_missing_the_property_falls_back_rather_than_throwing()
    {
        var measureId = await ResolveMeasureIdAsync("""{ "ref": { "masterId": "m1" }, "config": {} }""");

        measureId.Should().Be("FHIRBridge-PIPELINE-SUCCESS");
    }
}

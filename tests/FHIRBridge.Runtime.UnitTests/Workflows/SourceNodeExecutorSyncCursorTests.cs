using FHIRBridge.Runtime.Application.Abstractions.Connectors;
using FHIRBridge.Runtime.Application.Abstractions.Sources;
using FHIRBridge.Runtime.Application.DTOs;
using FHIRBridge.Runtime.Application.Workflows;
using FHIRBridge.Runtime.Application.Workflows.Catalog;
using FHIRBridge.Runtime.Domain.Enums;
using FHIRBridge.Runtime.Domain.Exceptions;
using FHIRBridge.Runtime.Domain.Workflows;
using FHIRBridge.Runtime.Infrastructure.Workflows.Executors;
using FluentAssertions;
using Moq;
using ResourceEnvelope = FHIRBridge.Runtime.Domain.ValueObjects.ResourceEnvelope;

namespace FHIRBridge.Runtime.UnitTests.Workflows;

// Each resource type in a search-REST source node is fetched via its own independent request, so each must carry its
// own _lastUpdated watermark and advance its own cursor entry, rather than sharing one connection-wide value — see
// SourceRetrievalConfiguration.LastSuccessfulSyncUtcByResourceType and SourceNodeExecutor's per-type typeSource.
public sealed class SourceNodeExecutorSyncCursorTests
{
    [Fact]
    public async Task Each_resource_type_carries_its_own_lastUpdated_watermark()
    {
        var sourceConnectionId = Guid.NewGuid();
        var source = new FhirSourceConfiguration(
            RuntimeSourceType.Epic, "Epic Sandbox", "https://fhir.example.com", null, "client-1", null, null, [],
            SourceConnectionId: sourceConnectionId,
            LastUpdatedWatermarks: new Dictionary<string, DateTime>
            {
                ["Patient"] = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                ["Observation"] = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            });

        var resolver = new Mock<ISourceConnectionRuntimeResolver>();
        resolver
            .Setup(x => x.ResolveAsync(sourceConnectionId, It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync(source);

        var client = new Mock<IFhirSourceClient>();
        client
            .Setup(x => x.SearchAsync("Patient", It.Is<FhirSourceConfiguration>(s => s.SearchParameters == "_lastUpdated=gt2026-01-01T00:00:00Z"), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<ResourceEnvelope>)[new ResourceEnvelope("Patient", "p1", "{}", null, null)]);
        client
            .Setup(x => x.SearchAsync("Observation", It.Is<FhirSourceConfiguration>(s => s.SearchParameters == "_lastUpdated=gt2026-06-01T00:00:00Z"), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<ResourceEnvelope>)[new ResourceEnvelope("Observation", "o1", "{}", null, null)]);

        var clientFactory = new Mock<IFhirSourceClientFactory>();
        clientFactory.Setup(x => x.Create(RuntimeSourceType.Epic)).Returns(client.Object);

        var executor = new EpicSourceNodeExecutor(clientFactory.Object, resolver.Object);
        var node = BuildNode(sourceConnectionId, "Patient,Observation");
        var context = new WorkflowExecutionContext(Guid.NewGuid(), "corr");

        await executor.ExecuteAsync(context, node, [], CancellationToken.None);

        client.VerifyAll();
    }

    [Fact]
    public async Task Cursor_store_is_advanced_only_for_resource_types_that_actually_succeeded()
    {
        var sourceConnectionId = Guid.NewGuid();
        var source = new FhirSourceConfiguration(
            RuntimeSourceType.Epic, "Epic Sandbox", "https://fhir.example.com", null, "client-1", null, null, [],
            SourceConnectionId: sourceConnectionId);

        var resolver = new Mock<ISourceConnectionRuntimeResolver>();
        resolver
            .Setup(x => x.ResolveAsync(sourceConnectionId, It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync(source);

        var client = new Mock<IFhirSourceClient>();
        client
            .Setup(x => x.SearchAsync("Patient", It.IsAny<FhirSourceConfiguration>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<ResourceEnvelope>)[new ResourceEnvelope("Patient", "p1", "{}", null, null)]);
        client
            .Setup(x => x.SearchAsync("Observation", It.IsAny<FhirSourceConfiguration>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ResourceAuthorizationException("Observation", 403, "not authorized"));
        client
            .Setup(x => x.SearchAsync("Condition", It.IsAny<FhirSourceConfiguration>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<ResourceEnvelope>)[new ResourceEnvelope("Condition", "c1", "{}", null, null)]);

        var clientFactory = new Mock<IFhirSourceClientFactory>();
        clientFactory.Setup(x => x.Create(RuntimeSourceType.Epic)).Returns(client.Object);

        var syncCursorStore = new Mock<ISourceConnectionSyncCursorStore>();
        syncCursorStore
            .Setup(x => x.RecordSuccessfulSyncAsync(sourceConnectionId, It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var executor = new EpicSourceNodeExecutor(clientFactory.Object, resolver.Object, syncCursorStore.Object);
        var node = BuildNode(sourceConnectionId, "Patient,Observation,Condition");
        var context = new WorkflowExecutionContext(Guid.NewGuid(), "corr");

        await executor.ExecuteAsync(context, node, [], CancellationToken.None);

        syncCursorStore.Verify(
            x => x.RecordSuccessfulSyncAsync(
                sourceConnectionId,
                It.Is<IReadOnlyCollection<string>>(types => types.Count == 2 && types.Contains("Patient") && types.Contains("Condition")),
                It.IsAny<DateTime>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    private static WorkflowNode BuildNode(Guid sourceConnectionId, string resources)
    {
        var workflow = new WorkflowDefinition(Guid.NewGuid(), "sync-cursor-test", 1);
        var configurationJson = $$"""{"sourceConnectionId":"{{sourceConnectionId}}","Resources":"{{resources}}"}""";
        return workflow.AddNode(WorkflowNodeTypes.EpicSource, WorkflowNodeCategory.Source, rank: 0, configurationJson: configurationJson);
    }
}

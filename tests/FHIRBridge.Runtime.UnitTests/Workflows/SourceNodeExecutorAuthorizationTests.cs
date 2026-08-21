using FHIRBridge.Runtime.Application.Abstractions.Connectors;
using FHIRBridge.Runtime.Application.Abstractions.Sources;
using FHIRBridge.Runtime.Application.DTOs;
using FHIRBridge.Runtime.Application.Workflows;
using FHIRBridge.Runtime.Application.Workflows.Catalog;
using FHIRBridge.Runtime.Application.Workflows.Payloads;
using FHIRBridge.Runtime.Domain.Enums;
using FHIRBridge.Runtime.Domain.Exceptions;
using FHIRBridge.Runtime.Domain.Workflows;
using FHIRBridge.Runtime.Infrastructure.Workflows.Executors;
using FluentAssertions;
using Moq;
using ResourceEnvelope = FHIRBridge.Runtime.Domain.ValueObjects.ResourceEnvelope;

namespace FHIRBridge.Runtime.UnitTests.Workflows;

// Covers the parent-vs-child scope-mismatch behavior: if the app selected in FHIRBridge isn't authorized (401/403)
// for the parent/cohort-seeding resource type (Patient), the whole node's fetch is cancelled since every sibling
// type is scoped off Patient's ids; if a non-parent (child) type is unauthorized, it's skipped and every other
// resource type this node fetches still completes normally.
public sealed class SourceNodeExecutorAuthorizationTests
{
    [Fact]
    public async Task Unauthorized_parent_resource_type_cancels_the_whole_node()
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
            .ThrowsAsync(new ResourceAuthorizationException("Patient", 403, "Epic FHIR request returned 403 (Forbidden) for .../Patient"));
        client
            .Setup(x => x.SearchAsync("Observation", It.IsAny<FhirSourceConfiguration>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<ResourceEnvelope>)[new ResourceEnvelope("Observation", "o1", "{}", null, null)]);

        var clientFactory = new Mock<IFhirSourceClientFactory>();
        clientFactory.Setup(x => x.Create(RuntimeSourceType.Epic)).Returns(client.Object);

        var executor = new EpicSourceNodeExecutor(clientFactory.Object, resolver.Object);
        var node = BuildNode(sourceConnectionId, "Patient,Observation");
        var context = new WorkflowExecutionContext(Guid.NewGuid(), "corr");

        var act = () => executor.ExecuteAsync(context, node, [], CancellationToken.None);

        var thrown = await act.Should().ThrowAsync<WorkflowRunCancelledException>();
        thrown.Which.ResourceType.Should().Be("Patient");
        thrown.Which.Message.Should().Contain("Patient");

        // Observation must never even be attempted — its search is scoped to Patient's ids, which never existed.
        client.Verify(x => x.SearchAsync("Observation", It.IsAny<FhirSourceConfiguration>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Unauthorized_child_resource_type_is_skipped_and_siblings_still_complete()
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
            .ThrowsAsync(new ResourceAuthorizationException("Observation", 403, "Epic FHIR request returned 403 (Forbidden) for .../Observation"));
        client
            .Setup(x => x.SearchAsync("Condition", It.IsAny<FhirSourceConfiguration>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<ResourceEnvelope>)[new ResourceEnvelope("Condition", "c1", "{}", null, null)]);

        var clientFactory = new Mock<IFhirSourceClientFactory>();
        clientFactory.Setup(x => x.Create(RuntimeSourceType.Epic)).Returns(client.Object);

        var executor = new EpicSourceNodeExecutor(clientFactory.Object, resolver.Object);
        var node = BuildNode(sourceConnectionId, "Patient,Observation,Condition");
        var context = new WorkflowExecutionContext(Guid.NewGuid(), "corr");

        var output = await executor.ExecuteAsync(context, node, [], CancellationToken.None);

        // Patient + Condition made it through; Observation contributed nothing but didn't abort the node.
        output.Payload.Should().BeOfType<ResourceBatch>().Which.Resources.Should().HaveCount(2);
        output.Metadata!["skippedResourceTypes"].Should().BeAssignableTo<string[]>()
            .Which.Should().ContainSingle(reason => reason.Contains("Observation") && reason.Contains("403"));
    }

    [Fact]
    public async Task Not_supported_child_resource_type_is_skipped_and_siblings_still_complete()
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
            .Setup(x => x.SearchAsync("MedicationAdministration", It.IsAny<FhirSourceConfiguration>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ResourceNotSupportedException(
                "MedicationAdministration", 400, "Epic FHIR request returned 400 (Bad Request) for .../MedicationAdministration"));
        client
            .Setup(x => x.SearchAsync("Condition", It.IsAny<FhirSourceConfiguration>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<ResourceEnvelope>)[new ResourceEnvelope("Condition", "c1", "{}", null, null)]);

        var clientFactory = new Mock<IFhirSourceClientFactory>();
        clientFactory.Setup(x => x.Create(RuntimeSourceType.Epic)).Returns(client.Object);

        var executor = new EpicSourceNodeExecutor(clientFactory.Object, resolver.Object);
        var node = BuildNode(sourceConnectionId, "Patient,MedicationAdministration,Condition");
        var context = new WorkflowExecutionContext(Guid.NewGuid(), "corr");

        var output = await executor.ExecuteAsync(context, node, [], CancellationToken.None);

        // Patient + Condition made it through; the not-supported resource type contributed nothing but didn't abort the node.
        output.Payload.Should().BeOfType<ResourceBatch>().Which.Resources.Should().HaveCount(2);
        output.Metadata!["skippedResourceTypes"].Should().BeAssignableTo<string[]>()
            .Which.Should().ContainSingle(reason => reason.Contains("MedicationAdministration") && reason.Contains("400"));
    }

    [Fact]
    public async Task No_skipped_resource_types_leaves_metadata_null()
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
            .Setup(x => x.SearchAsync("Observation", It.IsAny<FhirSourceConfiguration>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<ResourceEnvelope>)[new ResourceEnvelope("Observation", "o1", "{}", null, null)]);

        var clientFactory = new Mock<IFhirSourceClientFactory>();
        clientFactory.Setup(x => x.Create(RuntimeSourceType.Epic)).Returns(client.Object);

        var executor = new EpicSourceNodeExecutor(clientFactory.Object, resolver.Object);
        var node = BuildNode(sourceConnectionId, "Observation");
        var context = new WorkflowExecutionContext(Guid.NewGuid(), "corr");

        var output = await executor.ExecuteAsync(context, node, [], CancellationToken.None);

        output.Metadata!["skippedResourceTypes"].Should().BeNull();
    }

    private static WorkflowNode BuildNode(Guid sourceConnectionId, string resources)
    {
        var workflow = new WorkflowDefinition(Guid.NewGuid(), "auth-test", 1);
        var configurationJson = $$"""{"sourceConnectionId":"{{sourceConnectionId}}","Resources":"{{resources}}"}""";
        return workflow.AddNode(WorkflowNodeTypes.EpicSource, WorkflowNodeCategory.Source, rank: 0, configurationJson: configurationJson);
    }
}

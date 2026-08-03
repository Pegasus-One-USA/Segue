using FHIRBridge.Runtime.Application.Abstractions.Connectors;
using FHIRBridge.Runtime.Application.Abstractions.Sources;
using FHIRBridge.Runtime.Application.DTOs;
using FHIRBridge.Runtime.Application.Workflows;
using FHIRBridge.Runtime.Application.Workflows.Catalog;
using FHIRBridge.Runtime.Application.Workflows.Payloads;
using FHIRBridge.Runtime.Application.Workflows.Storage;
using FHIRBridge.Runtime.Domain.Enums;
using FHIRBridge.Runtime.Domain.Workflows;
using FHIRBridge.Runtime.Infrastructure.Workflows.Executors;
using FluentAssertions;
using Moq;
using ResourceEnvelope = FHIRBridge.Runtime.Domain.ValueObjects.ResourceEnvelope;

namespace FHIRBridge.Runtime.UnitTests.Workflows;

// Covers the "only fetch what a destination actually consumes" fix: a source node's own "Resources" field can list
// more resource types than any downstream destination selects (e.g. a destination wizard narrowed to just
// "Patient"), and the source must not over-fetch (or, upstream of this, over-request OAuth scopes for) the rest.
public sealed class SourceNodeExecutorDestinationRestrictionTests
{
    [Fact]
    public async Task Source_fetch_is_restricted_to_reachable_destination_resource_types()
    {
        var sourceConnectionId = Guid.NewGuid();
        var source = new FhirSourceConfiguration(
            RuntimeSourceType.Epic, "Epic Sandbox", "https://fhir.example.com", null, "client-1", null, null, [],
            SourceConnectionId: sourceConnectionId);

        var resolver = new Mock<ISourceConnectionRuntimeResolver>();
        resolver
            .Setup(x => x.ResolveAsync(sourceConnectionId, It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync(source);

        var typesFetched = new List<string>();
        var client = new Mock<IFhirSourceClient>();
        client
            .Setup(x => x.SearchAsync(It.IsAny<string>(), It.IsAny<FhirSourceConfiguration>(), It.IsAny<CancellationToken>()))
            .Returns((string type, FhirSourceConfiguration _, CancellationToken _) =>
            {
                typesFetched.Add(type);
                return Task.FromResult<IReadOnlyList<ResourceEnvelope>>([new ResourceEnvelope(type, "id-1", "{}", null, null)]);
            });

        var clientFactory = new Mock<IFhirSourceClientFactory>();
        clientFactory.Setup(x => x.Create(RuntimeSourceType.Epic)).Returns(client.Object);

        var workflowStore = new InMemoryWorkflowDefinitionStore();
        var workflow = new WorkflowDefinition(Guid.NewGuid(), "restriction-test", 1);
        var sourceNode = workflow.AddNode(
            WorkflowNodeTypes.EpicSource,
            WorkflowNodeCategory.Source,
            rank: 0,
            configurationJson: $$"""{"sourceConnectionId":"{{sourceConnectionId}}","Resources":"Patient, Observation, Condition"}""");
        var destinationNode = workflow.AddNode(
            WorkflowNodeTypes.SqlServerDestination,
            WorkflowNodeCategory.Destination,
            rank: 1,
            configurationJson: """{"dest_resources":"Patient"}""");
        workflow.AddEdge(sourceNode.Id, destinationNode.Id);
        await workflowStore.SaveAsync(workflow, CancellationToken.None);

        var executor = new EpicSourceNodeExecutor(
            clientFactory.Object, resolver.Object, workflowDefinitionStore: workflowStore);
        var context = new WorkflowExecutionContext(Guid.NewGuid(), "corr");

        var output = await executor.ExecuteAsync(context, sourceNode, [], CancellationToken.None);

        typesFetched.Should().BeEquivalentTo(["Patient"]);
        output.Payload.Should().BeOfType<ResourceBatch>().Which.Resources.Should().HaveCount(1);
    }

    [Fact]
    public async Task Source_fetch_is_unrestricted_when_no_destination_is_reachable()
    {
        var sourceConnectionId = Guid.NewGuid();
        var source = new FhirSourceConfiguration(
            RuntimeSourceType.Epic, "Epic Sandbox", "https://fhir.example.com", null, "client-1", null, null, [],
            SourceConnectionId: sourceConnectionId);

        var resolver = new Mock<ISourceConnectionRuntimeResolver>();
        resolver
            .Setup(x => x.ResolveAsync(sourceConnectionId, It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync(source);

        var typesFetched = new List<string>();
        var client = new Mock<IFhirSourceClient>();
        client
            .Setup(x => x.SearchAsync(It.IsAny<string>(), It.IsAny<FhirSourceConfiguration>(), It.IsAny<CancellationToken>()))
            .Returns((string type, FhirSourceConfiguration _, CancellationToken _) =>
            {
                typesFetched.Add(type);
                return Task.FromResult<IReadOnlyList<ResourceEnvelope>>([new ResourceEnvelope(type, "id-1", "{}", null, null)]);
            });

        var clientFactory = new Mock<IFhirSourceClientFactory>();
        clientFactory.Setup(x => x.Create(RuntimeSourceType.Epic)).Returns(client.Object);

        var workflowStore = new InMemoryWorkflowDefinitionStore();
        var workflow = new WorkflowDefinition(Guid.NewGuid(), "no-destination-test", 1);
        var sourceNode = workflow.AddNode(
            WorkflowNodeTypes.EpicSource,
            WorkflowNodeCategory.Source,
            rank: 0,
            configurationJson: $$"""{"sourceConnectionId":"{{sourceConnectionId}}","Resources":"Patient, Observation, Condition"}""");
        await workflowStore.SaveAsync(workflow, CancellationToken.None);

        var executor = new EpicSourceNodeExecutor(
            clientFactory.Object, resolver.Object, workflowDefinitionStore: workflowStore);
        var context = new WorkflowExecutionContext(Guid.NewGuid(), "corr");

        var output = await executor.ExecuteAsync(context, sourceNode, [], CancellationToken.None);

        typesFetched.Should().BeEquivalentTo(["Patient", "Observation", "Condition"]);
        output.Payload.Should().BeOfType<ResourceBatch>().Which.Resources.Should().HaveCount(3);
    }
}

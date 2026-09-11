using FHIRBridge.Application.Abstractions.Destinations;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Domain.Enums;
using FHIRBridge.Runtime.Application.Abstractions.Connectors;
using FHIRBridge.Runtime.Application.Abstractions.Sources;
using FHIRBridge.Runtime.Application.DTOs;
using FHIRBridge.Runtime.Application.Workflows;
using FHIRBridge.Runtime.Application.Workflows.Catalog;
using FHIRBridge.Runtime.Application.Workflows.Storage;
using FHIRBridge.Runtime.Domain.Enums;
using FHIRBridge.Runtime.Domain.Workflows;
using FHIRBridge.Runtime.Infrastructure.Workflows.Executors;
using FluentAssertions;
using Moq;
using ResourceEnvelope = FHIRBridge.Runtime.Domain.ValueObjects.ResourceEnvelope;

namespace FHIRBridge.Runtime.UnitTests.Workflows;

// Covers PipelineWriteContext.FetchMissingReferenceAsync wiring for MappedFhirRepositoryDestinationWriter's opt-in
// dest_autoFetchMissingReferences — DestinationNodeExecutor.ResolveFetchMissingReferenceDelegateAsync must find
// exactly one upstream source node in the workflow graph before ever building the delegate, falling back to null
// (today's unchanged blocking behavior) for zero or more than one candidate.
public sealed class AutoFetchMissingReferenceWiringTests
{
    private static (Mock<IConfiguredDestinationWriterFactory> WriterFactory, List<PipelineWriteContext> CapturedContexts) BuildWriterFactory()
    {
        var capturedContexts = new List<PipelineWriteContext>();
        var writer = new Mock<IConfiguredDestinationWriter>();
        writer.Setup(w => w.WriteAsync(
                It.IsAny<DestinationConfiguration>(), It.IsAny<MappingProfile>(),
                It.IsAny<IReadOnlyCollection<MappedDestinationRecord>>(), It.IsAny<PipelineWriteContext>(), It.IsAny<CancellationToken>()))
            .Callback<DestinationConfiguration, MappingProfile, IReadOnlyCollection<MappedDestinationRecord>, PipelineWriteContext, CancellationToken>(
                (_, _, _, writeContext, _) => capturedContexts.Add(writeContext))
            .ReturnsAsync(new DestinationWriteResult(0));

        var writerFactory = new Mock<IConfiguredDestinationWriterFactory>();
        writerFactory.Setup(f => f.Create(DestinationType.FhirRepository)).Returns(writer.Object);
        return (writerFactory, capturedContexts);
    }

    private static WorkflowNode AddDestinationNode(WorkflowDefinition workflow, int rank = 1) =>
        workflow.AddNode(
            WorkflowNodeTypes.FhirRepositoryDestination,
            WorkflowNodeCategory.Destination,
            rank,
            configurationJson: """{"dest_baseUrl":"https://aidbox.example.com/fhir","secretKeyVaultName":"kv","secretName":"secret"}""");

    private static WorkflowNode AddSourceNode(WorkflowDefinition workflow, Guid sourceConnectionId, int rank = 0) =>
        workflow.AddNode(
            WorkflowNodeTypes.EpicSource,
            WorkflowNodeCategory.Source,
            rank,
            configurationJson: $$"""{"sourceConnectionId":"{{sourceConnectionId}}"}""");

    [Fact]
    public async Task Exactly_one_upstream_source_node_wires_a_working_fetch_delegate()
    {
        var sourceConnectionId = Guid.NewGuid();
        var workflow = new WorkflowDefinition(Guid.NewGuid(), "auto-fetch-wiring-test", 1);
        var sourceNode = AddSourceNode(workflow, sourceConnectionId);
        var destinationNode = AddDestinationNode(workflow);
        workflow.AddEdge(sourceNode.Id, destinationNode.Id);
        var workflowStore = new InMemoryWorkflowDefinitionStore(TestHelpers.LicenseTestScopeFactory.Create());
        await workflowStore.SaveAsync(workflow, CancellationToken.None);

        var sourceConfig = new FhirSourceConfiguration(
            RuntimeSourceType.Epic, "Epic Sandbox", "https://fhir.example.com", null, "client-1", null, null, [],
            SourceConnectionId: sourceConnectionId);
        var resolver = new Mock<ISourceConnectionRuntimeResolver>();
        resolver
            .Setup(x => x.ResolveAsync(sourceConnectionId, null, null, It.IsAny<CancellationToken>(), null, null))
            .ReturnsAsync(sourceConfig);

        var client = new Mock<IFhirSourceClient>();
        client
            .Setup(x => x.ReadByIdAsync("Organization", "org1", sourceConfig, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ResourceEnvelope("Organization", "org1", """{"resourceType":"Organization","id":"org1"}""", null, null));

        var clientFactory = new Mock<IFhirSourceClientFactory>();
        clientFactory.Setup(f => f.Create(RuntimeSourceType.Epic)).Returns(client.Object);

        var (writerFactory, capturedContexts) = BuildWriterFactory();
        var executor = new FhirRepositoryDestinationNodeExecutor(
            writerFactory.Object, workflowStore, sourceClientFactory: clientFactory.Object, sourceConnectionResolver: resolver.Object);

        await executor.ExecuteAsync(new WorkflowExecutionContext(Guid.NewGuid(), "corr"), destinationNode, [], CancellationToken.None);

        capturedContexts.Should().ContainSingle();
        capturedContexts[0].FetchMissingReferenceAsync.Should().NotBeNull();

        var json = await capturedContexts[0].FetchMissingReferenceAsync!("Organization", "org1", CancellationToken.None);

        json.Should().Be("""{"resourceType":"Organization","id":"org1"}""");
        client.Verify(x => x.ReadByIdAsync("Organization", "org1", sourceConfig, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task No_upstream_source_node_leaves_the_fetch_delegate_null()
    {
        var workflow = new WorkflowDefinition(Guid.NewGuid(), "auto-fetch-wiring-test-no-source", 1);
        var destinationNode = AddDestinationNode(workflow, rank: 0);
        var workflowStore = new InMemoryWorkflowDefinitionStore(TestHelpers.LicenseTestScopeFactory.Create());
        await workflowStore.SaveAsync(workflow, CancellationToken.None);

        var (writerFactory, capturedContexts) = BuildWriterFactory();
        var executor = new FhirRepositoryDestinationNodeExecutor(
            writerFactory.Object, workflowStore,
            sourceClientFactory: Mock.Of<IFhirSourceClientFactory>(), sourceConnectionResolver: Mock.Of<ISourceConnectionRuntimeResolver>());

        await executor.ExecuteAsync(new WorkflowExecutionContext(Guid.NewGuid(), "corr"), destinationNode, [], CancellationToken.None);

        capturedContexts.Should().ContainSingle();
        capturedContexts[0].FetchMissingReferenceAsync.Should().BeNull();
    }

    [Fact]
    public async Task Two_upstream_source_nodes_leave_the_fetch_delegate_null_ambiguity_is_never_guessed()
    {
        var workflow = new WorkflowDefinition(Guid.NewGuid(), "auto-fetch-wiring-test-two-sources", 1);
        var sourceOne = AddSourceNode(workflow, Guid.NewGuid(), rank: 0);
        var sourceTwo = AddSourceNode(workflow, Guid.NewGuid(), rank: 0);
        var destinationNode = AddDestinationNode(workflow);
        workflow.AddEdge(sourceOne.Id, destinationNode.Id);
        workflow.AddEdge(sourceTwo.Id, destinationNode.Id);
        var workflowStore = new InMemoryWorkflowDefinitionStore(TestHelpers.LicenseTestScopeFactory.Create());
        await workflowStore.SaveAsync(workflow, CancellationToken.None);

        var (writerFactory, capturedContexts) = BuildWriterFactory();
        var executor = new FhirRepositoryDestinationNodeExecutor(
            writerFactory.Object, workflowStore,
            sourceClientFactory: Mock.Of<IFhirSourceClientFactory>(), sourceConnectionResolver: Mock.Of<ISourceConnectionRuntimeResolver>());

        await executor.ExecuteAsync(new WorkflowExecutionContext(Guid.NewGuid(), "corr"), destinationNode, [], CancellationToken.None);

        capturedContexts.Should().ContainSingle();
        capturedContexts[0].FetchMissingReferenceAsync.Should().BeNull();
    }

    [Fact]
    public async Task No_source_side_dependencies_injected_leaves_the_fetch_delegate_null_even_with_a_single_clean_upstream_source()
    {
        // Same graph shape as the successful-wiring test above, but this destination never received the two new
        // optional constructor dependencies (mirrors every non-FhirRepository destination type today, which never
        // passes them at all) — must fall back to null exactly like the ambiguous/absent-source cases.
        var sourceConnectionId = Guid.NewGuid();
        var workflow = new WorkflowDefinition(Guid.NewGuid(), "auto-fetch-wiring-test-no-deps", 1);
        var sourceNode = AddSourceNode(workflow, sourceConnectionId);
        var destinationNode = AddDestinationNode(workflow);
        workflow.AddEdge(sourceNode.Id, destinationNode.Id);
        var workflowStore = new InMemoryWorkflowDefinitionStore(TestHelpers.LicenseTestScopeFactory.Create());
        await workflowStore.SaveAsync(workflow, CancellationToken.None);

        var (writerFactory, capturedContexts) = BuildWriterFactory();
        var executor = new FhirRepositoryDestinationNodeExecutor(writerFactory.Object, workflowStore);

        await executor.ExecuteAsync(new WorkflowExecutionContext(Guid.NewGuid(), "corr"), destinationNode, [], CancellationToken.None);

        capturedContexts.Should().ContainSingle();
        capturedContexts[0].FetchMissingReferenceAsync.Should().BeNull();
    }
}

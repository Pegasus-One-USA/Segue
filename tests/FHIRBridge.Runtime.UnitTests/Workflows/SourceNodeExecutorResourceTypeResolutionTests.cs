using FHIRBridge.Runtime.Application.Abstractions.Connectors;
using FHIRBridge.Runtime.Application.Abstractions.Sources;
using FHIRBridge.Runtime.Application.DTOs;
using FHIRBridge.Runtime.Application.Workflows;
using FHIRBridge.Runtime.Application.Workflows.Catalog;
using FHIRBridge.Runtime.Domain.Enums;
using FHIRBridge.Runtime.Domain.Workflows;
using FHIRBridge.Runtime.Infrastructure.Workflows.Executors;
using FluentAssertions;
using Moq;

namespace FHIRBridge.Runtime.UnitTests.Workflows;

// Covers the resource-type resolution fallback chain in SourceNodeExecutor.ExecuteAsync: previously a node with
// none of "Resources", connection-level ResourceTypes, or SMART scopes silently defaulted to fetching only
// "Patient" — an easy-to-miss under-fetch. It must now fail loudly instead, and must still resolve correctly
// when a node config explicitly sets a single "resourceType".
public sealed class SourceNodeExecutorResourceTypeResolutionTests
{
    [Fact]
    public async Task Node_with_no_resolvable_resource_type_throws_instead_of_defaulting_to_Patient()
    {
        var sourceConnectionId = Guid.NewGuid();
        var source = new FhirSourceConfiguration(
            RuntimeSourceType.Epic, "Epic Sandbox", "https://fhir.example.com", null, "client-1", null, null, [],
            SourceConnectionId: sourceConnectionId);

        var resolver = new Mock<ISourceConnectionRuntimeResolver>();
        resolver
            .Setup(x => x.ResolveAsync(sourceConnectionId, It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync(source);

        var clientFactory = new Mock<IFhirSourceClientFactory>();
        clientFactory.Setup(x => x.Create(RuntimeSourceType.Epic)).Returns(new Mock<IFhirSourceClient>().Object);

        var executor = new EpicSourceNodeExecutor(clientFactory.Object, resolver.Object, null, null);
        var workflow = new WorkflowDefinition(Guid.NewGuid(), "no-resource-type-test", 1);
        var node = workflow.AddNode(
            WorkflowNodeTypes.EpicSource,
            WorkflowNodeCategory.Source,
            rank: 0,
            configurationJson: $$"""{"sourceConnectionId":"{{sourceConnectionId}}"}""");
        var context = new WorkflowExecutionContext(Guid.NewGuid(), "corr");

        var act = () => executor.ExecuteAsync(context, node, [], CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*has no resolvable FHIR resource type*");
    }

    [Fact]
    public async Task Node_with_explicit_single_resourceType_still_resolves_without_throwing()
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
            .ReturnsAsync((IReadOnlyList<FHIRBridge.Runtime.Domain.ValueObjects.ResourceEnvelope>)
                [new FHIRBridge.Runtime.Domain.ValueObjects.ResourceEnvelope("Patient", "p1", "{}", null, null)]);

        var clientFactory = new Mock<IFhirSourceClientFactory>();
        clientFactory.Setup(x => x.Create(RuntimeSourceType.Epic)).Returns(client.Object);

        var executor = new EpicSourceNodeExecutor(clientFactory.Object, resolver.Object, null, null);
        var workflow = new WorkflowDefinition(Guid.NewGuid(), "explicit-resource-type-test", 1);
        var node = workflow.AddNode(
            WorkflowNodeTypes.EpicSource,
            WorkflowNodeCategory.Source,
            rank: 0,
            configurationJson: $$"""{"resourceType":"Patient","sourceConnectionId":"{{sourceConnectionId}}"}""");
        var context = new WorkflowExecutionContext(Guid.NewGuid(), "corr");

        var output = await executor.ExecuteAsync(context, node, [], CancellationToken.None);

        output.Metadata!["resourceType"].Should().Be("Patient");
    }
}

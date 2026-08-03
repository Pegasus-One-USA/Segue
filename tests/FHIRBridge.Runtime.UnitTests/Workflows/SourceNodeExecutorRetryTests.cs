using FHIRBridge.Runtime.Application.Abstractions.Connectors;
using FHIRBridge.Runtime.Application.Abstractions.Sources;
using FHIRBridge.Runtime.Application.DTOs;
using FHIRBridge.Runtime.Application.Workflows;
using FHIRBridge.Runtime.Application.Workflows.Catalog;
using FHIRBridge.Runtime.Application.Workflows.Payloads;
using FHIRBridge.Runtime.Domain.Enums;
using FHIRBridge.Runtime.Domain.Workflows;
using FHIRBridge.Runtime.Infrastructure.Workflows.Executors;
using FluentAssertions;
using Moq;
using ResourceEnvelope = FHIRBridge.Runtime.Domain.ValueObjects.ResourceEnvelope;

namespace FHIRBridge.Runtime.UnitTests.Workflows;

// Covers SourceNodeExecutor.SearchWithPolicyAsync: it retries a transient failure per the source's RetryPolicy
// (fixed-3/exponential) before giving up, backing off between attempts, and returns the successful result once
// a retry succeeds.
public sealed class SourceNodeExecutorRetryTests
{
    [Fact]
    public async Task Fetch_that_fails_once_then_succeeds_retries_and_returns_the_result()
    {
        var sourceConnectionId = Guid.NewGuid();
        var source = new FhirSourceConfiguration(
            RuntimeSourceType.Epic, "Epic Sandbox", "https://fhir.example.com", null, "client-1", null, null, [],
            SourceConnectionId: sourceConnectionId,
            RetryPolicy: "fixed-3");

        var resolver = new Mock<ISourceConnectionRuntimeResolver>();
        resolver
            .Setup(x => x.ResolveAsync(sourceConnectionId, It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync(source);

        var callCount = 0;
        var client = new Mock<IFhirSourceClient>();
        client
            .Setup(x => x.SearchAsync("Patient", It.IsAny<FhirSourceConfiguration>(), It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                callCount++;
                if (callCount == 1)
                {
                    throw new InvalidOperationException("transient failure");
                }

                return Task.FromResult<IReadOnlyList<ResourceEnvelope>>(
                    [new ResourceEnvelope("Patient", "p1", "{}", null, null)]);
            });

        var clientFactory = new Mock<IFhirSourceClientFactory>();
        clientFactory.Setup(x => x.Create(RuntimeSourceType.Epic)).Returns(client.Object);

        var executor = new EpicSourceNodeExecutor(clientFactory.Object, resolver.Object, null, null);
        var node = BuildNode(sourceConnectionId);
        var context = new WorkflowExecutionContext(Guid.NewGuid(), "corr");

        var output = await executor.ExecuteAsync(context, node, [], CancellationToken.None);

        callCount.Should().Be(2);
        output.Payload.Should().BeOfType<ResourceBatch>().Which.Resources.Should().ContainSingle();
    }

    private static WorkflowNode BuildNode(Guid sourceConnectionId)
    {
        var workflow = new WorkflowDefinition(Guid.NewGuid(), "retry-test", 1);
        var configurationJson = $$"""{"resourceType":"Patient","sourceConnectionId":"{{sourceConnectionId}}"}""";
        return workflow.AddNode(WorkflowNodeTypes.EpicSource, WorkflowNodeCategory.Source, rank: 0, configurationJson: configurationJson);
    }
}

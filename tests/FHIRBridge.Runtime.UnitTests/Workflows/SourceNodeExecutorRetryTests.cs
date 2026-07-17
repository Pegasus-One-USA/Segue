using FHIRBridge.Application.Abstractions.Audit;
using FHIRBridge.Application.DTOs;
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

// Covers the P2 retry-visibility instrumentation: SourceNodeExecutor.SearchWithPolicyAsync retries a transient
// failure per the source's RetryPolicy, and now also reports each attempt to the Operational Log (Warning on
// retry, Information once a retry succeeds) so a "SQL Timeout → Retry 1 → Retry Successful" style timeline is
// actually visible instead of only being silently retried.
public sealed class SourceNodeExecutorRetryTests
{
    [Fact]
    public async Task Fetch_that_fails_once_then_succeeds_records_a_warning_then_an_information_entry()
    {
        var sourceConnectionId = Guid.NewGuid();
        var source = new FhirSourceConfiguration(
            RuntimeSourceType.Epic, "Epic Sandbox", "https://fhir.example.com", null, "client-1", null, null, [],
            SourceConnectionId: sourceConnectionId,
            RetryPolicy: "fixed-3");

        var resolver = new Mock<ISourceConnectionRuntimeResolver>();
        resolver
            .Setup(x => x.ResolveAsync(sourceConnectionId, It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>(), It.IsAny<string?>()))
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

        var recorded = new List<RecordOperationalAuditLogRequest>();
        var auditService = new Mock<IOperationalAuditService>();
        auditService
            .Setup(x => x.RecordAsync(It.IsAny<RecordOperationalAuditLogRequest>(), It.IsAny<CancellationToken>()))
            .Callback<RecordOperationalAuditLogRequest, CancellationToken>((request, _) => recorded.Add(request))
            .Returns(Task.CompletedTask);

        var executor = new EpicSourceNodeExecutor(clientFactory.Object, resolver.Object, null, null, auditService.Object);
        var node = BuildNode(sourceConnectionId);
        var context = new WorkflowExecutionContext(Guid.NewGuid(), "corr");

        var output = await executor.ExecuteAsync(context, node, [], CancellationToken.None);

        callCount.Should().Be(2);
        output.Payload.Should().BeOfType<ResourceBatch>().Which.Resources.Should().ContainSingle();

        recorded.Should().HaveCount(2);
        recorded[0].Severity.Should().Be(OperationalLogSeverities.Warning);
        recorded[0].Action.Should().Be("ResourceFetchRetried");
        recorded[0].PipelineRunId.Should().Be(context.WorkflowRunId);
        recorded[1].Severity.Should().Be(OperationalLogSeverities.Information);
        recorded[1].Action.Should().Be("ResourceFetchRetrySucceeded");
    }

    [Fact]
    public async Task Fetch_that_succeeds_first_try_records_no_retry_entries()
    {
        var sourceConnectionId = Guid.NewGuid();
        var source = new FhirSourceConfiguration(
            RuntimeSourceType.Epic, "Epic Sandbox", "https://fhir.example.com", null, "client-1", null, null, [],
            SourceConnectionId: sourceConnectionId,
            RetryPolicy: "fixed-3");

        var resolver = new Mock<ISourceConnectionRuntimeResolver>();
        resolver
            .Setup(x => x.ResolveAsync(sourceConnectionId, It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>(), It.IsAny<string?>()))
            .ReturnsAsync(source);

        var client = new Mock<IFhirSourceClient>();
        client
            .Setup(x => x.SearchAsync("Patient", It.IsAny<FhirSourceConfiguration>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<ResourceEnvelope>)[new ResourceEnvelope("Patient", "p1", "{}", null, null)]);

        var clientFactory = new Mock<IFhirSourceClientFactory>();
        clientFactory.Setup(x => x.Create(RuntimeSourceType.Epic)).Returns(client.Object);

        var auditService = new Mock<IOperationalAuditService>();

        var executor = new EpicSourceNodeExecutor(clientFactory.Object, resolver.Object, null, null, auditService.Object);
        var node = BuildNode(sourceConnectionId);

        await executor.ExecuteAsync(new WorkflowExecutionContext(Guid.NewGuid(), "corr"), node, [], CancellationToken.None);

        auditService.Verify(
            x => x.RecordAsync(It.IsAny<RecordOperationalAuditLogRequest>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    private static WorkflowNode BuildNode(Guid sourceConnectionId)
    {
        var workflow = new WorkflowDefinition(Guid.NewGuid(), "retry-test", 1);
        var configurationJson = $$"""{"resourceType":"Patient","sourceConnectionId":"{{sourceConnectionId}}"}""";
        return workflow.AddNode(WorkflowNodeTypes.EpicSource, WorkflowNodeCategory.Source, rank: 0, configurationJson: configurationJson);
    }
}

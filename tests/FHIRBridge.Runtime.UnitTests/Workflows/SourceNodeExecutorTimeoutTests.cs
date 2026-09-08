using System.Diagnostics;
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

/// <summary>
/// A connection's Timeout (seconds) bounds ONE request, not the whole resource-type extraction.
/// <c>IFhirSourceClient.SearchAsync</c> pages internally, so a CancelAfter around it was a cumulative deadline
/// across every page: a source returning healthy pages in a few seconds each blew the budget partway through and
/// the in-flight page died mid-socket (TaskCanceledException -> IOException -> SocketException), which looks like a
/// network fault but was self-inflicted. Observed against eCW staging, whose pages take 2-8s: a 30s timeout
/// cancelled Observation at exactly 30s cumulative on two of three attempts.
/// </summary>
public sealed class SourceNodeExecutorTimeoutTests
{
    private const int TimeoutSeconds = 1;

    // Longer than TimeoutSeconds in total, but no single "page" wait exceeds it — the shape the old cumulative
    // budget killed and a per-request budget must allow.
    private static readonly TimeSpan PageDelay = TimeSpan.FromMilliseconds(400);
    private const int PageCount = 4;

    [Fact]
    public async Task Slow_but_progressing_multi_page_extraction_is_not_cancelled_by_the_per_request_timeout()
    {
        var sourceConnectionId = Guid.NewGuid();
        var source = new FhirSourceConfiguration(
            RuntimeSourceType.Epic, "Epic Sandbox", "https://fhir.example.com", null, "client-1", null, null, [],
            SourceConnectionId: sourceConnectionId, TimeoutSeconds: TimeoutSeconds);

        var resolver = new Mock<ISourceConnectionRuntimeResolver>();
        resolver
            .Setup(x => x.ResolveAsync(sourceConnectionId, It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync(source);

        // Stands in for the connector's paging loop: several sequential waits, each well inside the timeout,
        // honoring the token it was handed exactly as a real HttpClient send would.
        var client = new Mock<IFhirSourceClient>();
        client
            .Setup(x => x.SearchAsync("Observation", It.IsAny<FhirSourceConfiguration>(), It.IsAny<CancellationToken>()))
            .Returns(async (string _, FhirSourceConfiguration _, CancellationToken token) =>
            {
                for (var page = 0; page < PageCount; page++)
                {
                    await Task.Delay(PageDelay, token);
                }

                return (IReadOnlyList<ResourceEnvelope>)[new ResourceEnvelope("Observation", "o1", "{}", null, null)];
            });

        var clientFactory = new Mock<IFhirSourceClientFactory>();
        clientFactory.Setup(x => x.Create(RuntimeSourceType.Epic)).Returns(client.Object);

        var executor = new EpicSourceNodeExecutor(clientFactory.Object, resolver.Object);
        var context = new WorkflowExecutionContext(Guid.NewGuid(), "corr");

        var stopwatch = Stopwatch.StartNew();
        var output = await executor.ExecuteAsync(context, BuildNode(sourceConnectionId, "Observation"), [], CancellationToken.None);
        stopwatch.Stop();

        // It really did outlive the timeout rather than short-circuiting, and still returned its resources.
        stopwatch.Elapsed.Should().BeGreaterThan(TimeSpan.FromSeconds(TimeoutSeconds));
        var batch = output.Payload.Should().BeOfType<ResourceBatch>().Subject;
        batch.Resources.Should().ContainSingle().Which.ResourceType.Should().Be("Observation");
    }

    [Fact]
    public async Task Outer_cancellation_still_stops_the_extraction()
    {
        // Removing the cumulative budget must not make an extraction uninterruptible — a cancelled run (or a
        // shutting-down host) still has to tear the fetch down.
        var sourceConnectionId = Guid.NewGuid();
        var source = new FhirSourceConfiguration(
            RuntimeSourceType.Epic, "Epic Sandbox", "https://fhir.example.com", null, "client-1", null, null, [],
            SourceConnectionId: sourceConnectionId, TimeoutSeconds: 600);

        var resolver = new Mock<ISourceConnectionRuntimeResolver>();
        resolver
            .Setup(x => x.ResolveAsync(sourceConnectionId, It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync(source);

        var client = new Mock<IFhirSourceClient>();
        client
            .Setup(x => x.SearchAsync("Observation", It.IsAny<FhirSourceConfiguration>(), It.IsAny<CancellationToken>()))
            .Returns(async (string _, FhirSourceConfiguration _, CancellationToken token) =>
            {
                await Task.Delay(TimeSpan.FromSeconds(30), token);
                return (IReadOnlyList<ResourceEnvelope>)[];
            });

        var clientFactory = new Mock<IFhirSourceClientFactory>();
        clientFactory.Setup(x => x.Create(RuntimeSourceType.Epic)).Returns(client.Object);

        var executor = new EpicSourceNodeExecutor(clientFactory.Object, resolver.Object);
        var context = new WorkflowExecutionContext(Guid.NewGuid(), "corr");

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        var act = () => executor.ExecuteAsync(context, BuildNode(sourceConnectionId, "Observation"), [], cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    private static WorkflowNode BuildNode(Guid sourceConnectionId, string resources)
    {
        var workflow = new WorkflowDefinition(Guid.NewGuid(), "timeout-test", 1);
        var configurationJson = $$"""{"sourceConnectionId":"{{sourceConnectionId}}","Resources":"{{resources}}"}""";
        return workflow.AddNode(WorkflowNodeTypes.EpicSource, WorkflowNodeCategory.Source, rank: 0, configurationJson: configurationJson);
    }
}

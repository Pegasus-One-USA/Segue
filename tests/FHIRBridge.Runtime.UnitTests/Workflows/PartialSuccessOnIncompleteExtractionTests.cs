using System.Net;
using System.Text;
using FHIRBridge.Runtime.Application.Abstractions.Auth;
using FHIRBridge.Runtime.Application.Abstractions.Connectors;
using FHIRBridge.Runtime.Application.Abstractions.Sources;
using FHIRBridge.Runtime.Application.DTOs;
using FHIRBridge.Runtime.Application.Workflows;
using FHIRBridge.Runtime.Application.Workflows.Catalog;
using FHIRBridge.Runtime.Domain.Enums;
using FHIRBridge.Runtime.Domain.Workflows;
using FHIRBridge.Runtime.Infrastructure.Connectors;
using FHIRBridge.Runtime.Infrastructure.Workflows.Executors;
using FluentAssertions;
using Moq;

namespace FHIRBridge.Runtime.UnitTests.Workflows;

/// <summary>
/// A connector can lose data without failing the call: one value of a fanned-out search rejected, or a page cap
/// cutting a paged fetch short. Neither throws, and the resource list looks identical whether it holds everything
/// or a fraction — which is how a live eCW run extracted 28 of 55 Observations and finished Succeeded with six
/// rejected categories recorded nowhere but a log line. These pin the loss onto the run's skippedResourceTypes,
/// the channel RankedWorkflowOrchestrator turns into WorkflowRunStatus.PartialSuccess.
/// </summary>
public sealed class PartialSuccessOnIncompleteExtractionTests
{
    private const string ObservationBundle =
        "{\"resourceType\":\"Bundle\",\"type\":\"searchset\",\"entry\":[{\"resource\":" +
        "{\"resourceType\":\"Observation\",\"id\":\"obs-lab-1\",\"status\":\"final\"}}]}";

    [Fact]
    public async Task A_rejected_fanout_category_is_reported_as_partial_success_not_a_clean_run()
    {
        // Only laboratory answers; the other six categories 401 exactly as they did once the access token expired
        // mid-run. The fan-out keeps laboratory's result and skips the rest — correct, but it must not be silent.
        var handler = new CategoryHandler(succeedFor: "laboratory");
        var output = await ExecuteAsync(handler, maxPages: 1000);

        var skipped = output.Metadata["skippedResourceTypes"].Should().BeOfType<string[]>().Subject;

        skipped.Should().HaveCount(6, "six categories were rejected and each is a distinct loss");
        skipped.Should().OnlyContain(reason => reason.StartsWith("Observation:"));
        skipped.Should().Contain(
            reason => reason.Contains("category=survey"),
            "the survey category is the one that silently cost 25 records");
    }

    [Fact]
    public async Task A_fully_successful_extraction_still_reports_nothing_skipped()
    {
        // The guard against crying wolf: every category answering must leave the run a clean success.
        var handler = new CategoryHandler(succeedFor: null);
        var output = await ExecuteAsync(handler, maxPages: 1000);

        output.Metadata["skippedResourceTypes"].Should().BeNull();
    }

    [Fact]
    public async Task Paging_cut_short_by_the_page_cap_is_reported_as_partial_success()
    {
        // Distinct from a rejected category: every request succeeded, the cap simply ran out while the server was
        // still offering more. A short result reported as complete is the same failure either way.
        var handler = new AdvancingPagesHandler(totalPages: 5);
        var output = await ExecuteAsync(handler, maxPages: 2);

        var skipped = output.Metadata["skippedResourceTypes"].Should().BeOfType<string[]>().Subject;

        skipped.Should().Contain(reason => reason.Contains("page cap"));
        skipped.Should().Contain(reason => reason.Contains("Max Records Per Run"));
    }

    private static async Task<WorkflowNodeOutput> ExecuteAsync(HttpMessageHandler handler, int maxPages)
    {
        var sourceConnectionId = Guid.NewGuid();
        var source = new FhirSourceConfiguration(
            RuntimeSourceType.Healow, "eCW", "https://staging-fhir.ecwcloud.com/fhir/r4/FFBJCD",
            "https://auth/token", "client-1", null, null, [],
            SearchCount: 100, MaxPages: maxPages, SourceConnectionId: sourceConnectionId);

        var resolver = new Mock<ISourceConnectionRuntimeResolver>();
        resolver
            .Setup(x => x.ResolveAsync(sourceConnectionId, It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<CancellationToken>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync(source);

        // The REAL connector — the fan-out, the page cap and the diagnostics under test all live in it.
        var client = new EClinicalWorksFhirSourceClient(new HttpClient(handler), new StubTokenProvider());
        var clientFactory = new Mock<IFhirSourceClientFactory>();
        clientFactory.Setup(x => x.Create(RuntimeSourceType.Healow)).Returns(client);

        var executor = new EClinicalWorksSourceNodeExecutor(clientFactory.Object, resolver.Object);

        var workflow = new WorkflowDefinition(Guid.NewGuid(), "partial-success-test", 1);
        var configurationJson =
            "{\"sourceConnectionId\":\"" + sourceConnectionId + "\",\"Resources\":\"Observation\"}";
        var node = workflow.AddNode(
            WorkflowNodeTypes.EClinicalWorksSource, WorkflowNodeCategory.Source, rank: 0,
            configurationJson: configurationJson);

        return await executor.ExecuteAsync(
            new WorkflowExecutionContext(Guid.NewGuid(), "corr"), node, [], CancellationToken.None);
    }

    /// <summary>200 for the one category named; 401 for every other — an expired token, in effect.</summary>
    private sealed class CategoryHandler : HttpMessageHandler
    {
        private readonly string? _succeedFor;

        public CategoryHandler(string? succeedFor) => _succeedFor = succeedFor;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!.ToString();
            var ok = _succeedFor is null || uri.Contains("category=" + _succeedFor, StringComparison.Ordinal);

            return Task.FromResult(ok
                ? new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(ObservationBundle, Encoding.UTF8, "application/fhir+json"),
                }
                : new HttpResponseMessage(HttpStatusCode.Unauthorized)
                {
                    Content = new StringContent(
                        "{\"resourceType\":\"OperationOutcome\"}", Encoding.UTF8, "application/fhir+json"),
                });
        }
    }

    /// <summary>Genuinely advancing pages of unique records — so only the cap can stop it.</summary>
    private sealed class AdvancingPagesHandler : HttpMessageHandler
    {
        private readonly int _totalPages;
        private int _served;

        public AdvancingPagesHandler(int totalPages) => _totalPages = totalPages;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _served++;
            var link = _served >= _totalPages
                ? ""
                : ",\"link\":[{\"relation\":\"next\",\"url\":\"https://staging-fhir.ecwcloud.com/fhir/r4/FFBJCD/" +
                  "Observation?page=" + (_served + 1) + "\"}]";

            var body = "{\"resourceType\":\"Bundle\",\"type\":\"searchset\"" + link +
                       ",\"entry\":[{\"resource\":{\"resourceType\":\"Observation\",\"id\":\"obs-" + _served +
                       "\",\"status\":\"final\"}}]}";

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/fhir+json"),
            });
        }
    }

    private sealed class StubTokenProvider : IFhirAccessTokenProvider
    {
        public Task<string> GetAccessTokenAsync(FhirSourceConfiguration source, CancellationToken cancellationToken) =>
            Task.FromResult("token");
    }
}

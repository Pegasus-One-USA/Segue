using System.Net;
using System.Text;
using FHIRBridge.Runtime.Application.Abstractions.Auth;
using FHIRBridge.Runtime.Application.DTOs;
using FHIRBridge.Runtime.Domain.Enums;
using FHIRBridge.Runtime.Infrastructure.Connectors;
using FluentAssertions;

namespace FHIRBridge.Runtime.UnitTests.Connectors;

/// <summary>
/// Paging must not stop early and report the short result as complete. The page cap used to be a hard-coded 5,
/// which with the default 100-record page silently capped every resource type at 500 records — no error, no
/// PartialSuccess, and no setting anywhere to raise it. These cover the cap actually being honoured when the
/// operator sets one, and paging running to completion when they don't.
/// </summary>
public sealed class SearchPagingLimitTests
{
    private static FhirSourceConfiguration Source(int searchCount, int maxPages) => new(
        RuntimeSourceType.Healow, "eCW", "https://fhir.example.com/r4", "https://auth/token", "client-1",
        null, null, [], SearchCount: searchCount, MaxPages: maxPages);

    [Fact]
    public async Task Pages_to_completion_when_the_cap_is_generous()
    {
        // Server offers 7 pages; the cap allows far more, so every page must be fetched.
        var handler = new PagingHandler(totalPages: 7);
        var client = new EClinicalWorksFhirSourceClient(new HttpClient(handler), new StubTokenProvider());

        var resources = await client.SearchAsync("Encounter", Source(searchCount: 100, maxPages: 1000), CancellationToken.None);

        handler.RequestCount.Should().Be(7);
        resources.Should().HaveCount(7);
    }

    [Fact]
    public async Task Stops_at_the_cap_when_the_operator_set_one()
    {
        // A cap the operator asked for is still honoured — this is the intended-truncation path.
        var handler = new PagingHandler(totalPages: 7);
        var client = new EClinicalWorksFhirSourceClient(new HttpClient(handler), new StubTokenProvider());

        var resources = await client.SearchAsync("Encounter", Source(searchCount: 100, maxPages: 3), CancellationToken.None);

        handler.RequestCount.Should().Be(3);
        resources.Should().HaveCount(3);
    }

    [Fact]
    public async Task A_cap_of_zero_or_less_still_fetches_one_page_rather_than_none()
    {
        // Guards the connector's existing `MaxPages <= 0 ? 1` coercion: a misconfigured 0 must not mean
        // "extract nothing at all", which would look identical to a patient having no data.
        var handler = new PagingHandler(totalPages: 7);
        var client = new EClinicalWorksFhirSourceClient(new HttpClient(handler), new StubTokenProvider());

        var resources = await client.SearchAsync("Encounter", Source(searchCount: 100, maxPages: 0), CancellationToken.None);

        handler.RequestCount.Should().Be(1);
        resources.Should().HaveCount(1);
    }

    /// <summary>Returns one resource per page, with a next link until <c>totalPages</c> have been served.</summary>
    private sealed class PagingHandler : HttpMessageHandler
    {
        private readonly int _totalPages;

        public PagingHandler(int totalPages) => _totalPages = totalPages;

        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            var isLast = RequestCount >= _totalPages;

            var link = isLast
                ? ""
                : ",\"link\":[{\"relation\":\"next\",\"url\":\"https://fhir.example.com/r4/Encounter?page=" +
                  (RequestCount + 1) + "\"}]";

            var body = "{\"resourceType\":\"Bundle\",\"type\":\"searchset\"" + link +
                       ",\"entry\":[{\"resource\":{\"resourceType\":\"Encounter\",\"id\":\"enc-" +
                       RequestCount + "\",\"status\":\"finished\"}}]}";

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

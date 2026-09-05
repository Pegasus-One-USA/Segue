using System.Net;
using System.Text;
using FHIRBridge.Runtime.Application.Abstractions.Auth;
using FHIRBridge.Runtime.Application.DTOs;
using FHIRBridge.Runtime.Domain.Enums;
using FHIRBridge.Runtime.Infrastructure.Connectors;
using FluentAssertions;

namespace FHIRBridge.Runtime.UnitTests.Connectors;

/// <summary>
/// eCW offers a "next" link even when the page it just served was complete, and following it re-serves the same
/// records — so paging never ends on its own. A live run made 31 successful Observation requests for 28 unique
/// laboratory records, ran ~4.5 minutes, blew past eCW's ~5-minute access token, then 401'd on the six remaining
/// categories, which the fan-out's per-category skip swallowed: 28 records reported as a successful run.
/// </summary>
public sealed class NonAdvancingPagingTests
{
    private static FhirSourceConfiguration Source(int maxPages = 1000) => new(
        RuntimeSourceType.Healow, "eCW", "https://fhir.example.com/r4", "https://auth/token", "client-1",
        null, null, [], SearchCount: 100, MaxPages: maxPages);

    [Fact]
    public async Task A_next_link_that_repeats_the_same_url_stops_paging()
    {
        var handler = new StuckPagingHandler(advanceUrl: false);
        var client = new EClinicalWorksFhirSourceClient(new HttpClient(handler), new StubTokenProvider());

        var resources = await client.SearchAsync("Encounter", Source(), CancellationToken.None);

        // Page 1, then the repeated URL once — recognised and stopped, not chased to the page cap.
        handler.RequestCount.Should().BeLessThan(4);
        resources.Should().HaveCount(2);
    }

    [Fact]
    public async Task A_next_link_that_advances_but_re_serves_the_same_records_stops_paging()
    {
        // The nastier shape: the URL keeps changing (offset increments) but the payload never does, so URL
        // comparison alone would chase it forever. Progress has to be judged on resource ids.
        var handler = new StuckPagingHandler(advanceUrl: true);
        var client = new EClinicalWorksFhirSourceClient(new HttpClient(handler), new StubTokenProvider());

        var resources = await client.SearchAsync("Encounter", Source(), CancellationToken.None);

        handler.RequestCount.Should().BeLessThan(4);
        resources.Should().HaveCount(2);
    }

    [Fact]
    public async Task A_token_expiring_mid_fetch_is_re_read_per_page_rather_than_401ing()
    {
        // The token provider hands out a new value once the first is "expired". Acquiring once per resource-type
        // fetch meant every page after expiry 401'd; per-page acquisition picks the fresh one up.
        var tokenProvider = new ExpiringTokenProvider(callsBeforeRotation: 2);
        var handler = new TokenCheckingHandler(tokenProvider, totalPages: 5);
        var client = new EClinicalWorksFhirSourceClient(new HttpClient(handler), tokenProvider);

        var resources = await client.SearchAsync("Encounter", Source(), CancellationToken.None);

        handler.UnauthorizedCount.Should().Be(0);
        resources.Should().HaveCount(5);
        tokenProvider.CallCount.Should().BeGreaterThan(1, "the token must be re-read as paging proceeds");
    }

    /// <summary>Always offers a next link, and always returns the same two resources.</summary>
    private sealed class StuckPagingHandler : HttpMessageHandler
    {
        private readonly bool _advanceUrl;

        public StuckPagingHandler(bool advanceUrl) => _advanceUrl = advanceUrl;

        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            var nextUrl = _advanceUrl
                ? "https://fhir.example.com/r4/Encounter?offset=" + RequestCount
                : "https://fhir.example.com/r4/Encounter?offset=stuck";

            var body = "{\"resourceType\":\"Bundle\",\"type\":\"searchset\"," +
                       "\"link\":[{\"relation\":\"next\",\"url\":\"" + nextUrl + "\"}]," +
                       "\"entry\":[" +
                       "{\"resource\":{\"resourceType\":\"Encounter\",\"id\":\"enc-1\",\"status\":\"finished\"}}," +
                       "{\"resource\":{\"resourceType\":\"Encounter\",\"id\":\"enc-2\",\"status\":\"finished\"}}]}";

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/fhir+json"),
            });
        }
    }

    /// <summary>401s any request not bearing the provider's current token — as an expired-token server would.</summary>
    private sealed class TokenCheckingHandler : HttpMessageHandler
    {
        private readonly ExpiringTokenProvider _tokenProvider;
        private readonly int _totalPages;
        private int _requestCount;

        public TokenCheckingHandler(ExpiringTokenProvider tokenProvider, int totalPages)
        {
            _tokenProvider = tokenProvider;
            _totalPages = totalPages;
        }

        public int UnauthorizedCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Headers.Authorization?.Parameter != _tokenProvider.CurrentToken)
            {
                UnauthorizedCount++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)
                {
                    Content = new StringContent("{}", Encoding.UTF8, "application/fhir+json"),
                });
            }

            _requestCount++;
            var isLast = _requestCount >= _totalPages;
            var link = isLast
                ? ""
                : ",\"link\":[{\"relation\":\"next\",\"url\":\"https://fhir.example.com/r4/Encounter?page=" +
                  (_requestCount + 1) + "\"}]";

            var body = "{\"resourceType\":\"Bundle\",\"type\":\"searchset\"" + link +
                       ",\"entry\":[{\"resource\":{\"resourceType\":\"Encounter\",\"id\":\"enc-" + _requestCount +
                       "\",\"status\":\"finished\"}}]}";

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/fhir+json"),
            });
        }
    }

    private sealed class ExpiringTokenProvider : IFhirAccessTokenProvider
    {
        private readonly int _callsBeforeRotation;

        public ExpiringTokenProvider(int callsBeforeRotation) => _callsBeforeRotation = callsBeforeRotation;

        public int CallCount { get; private set; }

        public string CurrentToken { get; private set; } = "token-1";

        public Task<string> GetAccessTokenAsync(FhirSourceConfiguration source, CancellationToken cancellationToken)
        {
            CallCount++;
            if (CallCount > _callsBeforeRotation)
            {
                CurrentToken = "token-2";
            }

            return Task.FromResult(CurrentToken);
        }
    }

    private sealed class StubTokenProvider : IFhirAccessTokenProvider
    {
        public Task<string> GetAccessTokenAsync(FhirSourceConfiguration source, CancellationToken cancellationToken) =>
            Task.FromResult("token");
    }
}

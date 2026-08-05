using System.Net;
using FHIRBridge.Infrastructure.Destinations.Auth;
using FHIRBridge.Runtime.Application.Abstractions.Auth;
using FluentAssertions;
using Moq;

namespace FHIRBridge.UnitTests.Destinations.Auth;

public sealed class FhirDestinationOAuth2TokenProviderTests
{
    [Fact]
    public async Task Requests_a_token_via_client_credentials_and_caches_it()
    {
        var handler = new CapturingHandler();
        var cache = new Mock<IFhirAccessTokenCache>();
        cache.Setup(c => c.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync((string?)null);

        var provider = new FhirDestinationOAuth2TokenProvider(new HttpClient(handler), cache.Object);
        var options = new FhirDestinationOAuth2Options("https://aidbox/auth/token", "cid", "csec", "system/*.write");

        var token = await provider.GetAccessTokenAsync(options, CancellationToken.None);

        token.Should().Be("minted-token");
        handler.Body.Should().Contain("grant_type=client_credentials");
        handler.Body.Should().Contain("client_id=cid");
        handler.Body.Should().Contain("client_secret=csec");
        handler.Body.Should().Contain("scope=system");
        cache.Verify(
            c => c.SetAsync(
                It.Is<string>(k => k.StartsWith("fhir-dest-token:") && k.Contains("cid")),
                "minted-token",
                It.IsAny<DateTimeOffset>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Returns_cached_token_without_calling_the_endpoint()
    {
        var handler = new CapturingHandler();
        var cache = new Mock<IFhirAccessTokenCache>();
        cache.Setup(c => c.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync("cached-token");

        var provider = new FhirDestinationOAuth2TokenProvider(new HttpClient(handler), cache.Object);
        var options = new FhirDestinationOAuth2Options("https://aidbox/auth/token", "cid", "csec", null);

        var token = await provider.GetAccessTokenAsync(options, CancellationToken.None);

        token.Should().Be("cached-token");
        handler.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task Non_success_response_throws_with_body_included()
    {
        var handler = new CapturingHandler(HttpStatusCode.Unauthorized, """{"error":"invalid_client"}""");
        var cache = new Mock<IFhirAccessTokenCache>();
        cache.Setup(c => c.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync((string?)null);

        var provider = new FhirDestinationOAuth2TokenProvider(new HttpClient(handler), cache.Object);
        var options = new FhirDestinationOAuth2Options("https://aidbox/auth/token", "cid", "csec", null);

        var act = () => provider.GetAccessTokenAsync(options, CancellationToken.None);

        var exception = await act.Should().ThrowAsync<InvalidOperationException>();
        exception.Which.Message.Should().Contain("invalid_client");
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _statusCode;
        private readonly string _responseBody;

        public string? Body { get; private set; }
        public int CallCount { get; private set; }

        public CapturingHandler(
            HttpStatusCode statusCode = HttpStatusCode.OK,
            string responseBody = """{ "access_token": "minted-token", "expires_in": 300 }""")
        {
            _statusCode = statusCode;
            _responseBody = responseBody;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(_statusCode) { Content = new StringContent(_responseBody) };
        }
    }
}

using System.Net;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Infrastructure.Destinations;
using FHIRBridge.Infrastructure.Destinations.Auth;
using FluentAssertions;
using Moq;

namespace FHIRBridge.UnitTests.Destinations;

// Covers FhirDestinationConnectionTestService's SMART .well-known/smart-configuration discovery — the wizard no
// longer collects a token endpoint from the user for "clientcredentials"/"oauth2"; it's discovered from BaseUrl
// instead (Aidbox-SmartConfig-Discovery-Plan.md). basic/bearer/none never need discovery at all.
public sealed class FhirDestinationConnectionTestServiceTests
{
    private const string BaseUrl = "https://aidbox.example.com/fhir";
    private const string DiscoveryUrl = $"{BaseUrl}/.well-known/smart-configuration";
    private const string MetadataUrl = $"{BaseUrl}/metadata";
    private const string DiscoveredTokenEndpoint = "https://aidbox.example.com/auth/token";

    private static IHttpClientFactory FactoryFor(RoutingHandler handler) =>
        Mock.Of<IHttpClientFactory>(f => f.CreateClient(It.IsAny<string>()) == new HttpClient(handler, false));

    [Fact]
    public async Task OAuth2_discovers_the_token_endpoint_and_uses_it_for_the_token_exchange()
    {
        var handler = new RoutingHandler(url => url switch
        {
            DiscoveryUrl => (HttpStatusCode.OK, $$"""{"token_endpoint":"{{DiscoveredTokenEndpoint}}"}"""),
            MetadataUrl => (HttpStatusCode.OK, "{}"),
            _ => (HttpStatusCode.NotFound, ""),
        });
        var tokenProvider = new Mock<IFhirDestinationTokenProvider>();
        tokenProvider.Setup(p => p.GetAccessTokenAsync(It.IsAny<FhirDestinationOAuth2Options>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("minted-token");

        var service = new FhirDestinationConnectionTestService(FactoryFor(handler), tokenProvider.Object);
        var request = new FhirConnectionTestRequest(BaseUrl, "oauth2", "cid", "csec", null, null, null);

        var result = await service.TestConnectionAsync(request, CancellationToken.None);

        result.Connected.Should().BeTrue();
        result.ResolvedTokenEndpoint.Should().Be(DiscoveredTokenEndpoint);
        tokenProvider.Verify(p => p.GetAccessTokenAsync(
            It.Is<FhirDestinationOAuth2Options>(o => o.TokenEndpoint == DiscoveredTokenEndpoint && o.ClientId == "cid" && o.ClientSecret == "csec"),
            It.IsAny<CancellationToken>()), Times.Once);
        handler.RequestedUrls.Should().Contain(DiscoveryUrl);
    }

    [Fact]
    public async Task ClientCredentials_auth_type_also_discovers_the_token_endpoint()
    {
        var handler = new RoutingHandler(url => url switch
        {
            DiscoveryUrl => (HttpStatusCode.OK, $$"""{"token_endpoint":"{{DiscoveredTokenEndpoint}}"}"""),
            MetadataUrl => (HttpStatusCode.OK, "{}"),
            _ => (HttpStatusCode.NotFound, ""),
        });
        var tokenProvider = new Mock<IFhirDestinationTokenProvider>();
        tokenProvider.Setup(p => p.GetAccessTokenAsync(It.IsAny<FhirDestinationOAuth2Options>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("minted-token");

        var service = new FhirDestinationConnectionTestService(FactoryFor(handler), tokenProvider.Object);
        var request = new FhirConnectionTestRequest(BaseUrl, "clientcredentials", "cid", "csec", null, null, null);

        var result = await service.TestConnectionAsync(request, CancellationToken.None);

        result.Connected.Should().BeTrue();
        result.ResolvedTokenEndpoint.Should().Be(DiscoveredTokenEndpoint);
    }

    [Fact]
    public async Task Discovery_failure_produces_an_error_distinguishable_from_a_credentials_failure()
    {
        var handler = new RoutingHandler(url => url switch
        {
            DiscoveryUrl => (HttpStatusCode.NotFound, ""),
            _ => (HttpStatusCode.OK, "{}"),
        });
        var tokenProvider = new Mock<IFhirDestinationTokenProvider>();
        var service = new FhirDestinationConnectionTestService(FactoryFor(handler), tokenProvider.Object);
        var request = new FhirConnectionTestRequest(BaseUrl, "oauth2", "cid", "csec", null, null, null);

        var result = await service.TestConnectionAsync(request, CancellationToken.None);

        result.Connected.Should().BeFalse();
        result.Error.Should().Contain("discover").And.Contain(DiscoveryUrl);
        tokenProvider.Verify(p => p.GetAccessTokenAsync(It.IsAny<FhirDestinationOAuth2Options>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Discovery_response_missing_token_endpoint_field_throws_a_clear_error()
    {
        var handler = new RoutingHandler(url => url switch
        {
            DiscoveryUrl => (HttpStatusCode.OK, """{"authorization_endpoint":"https://aidbox.example.com/auth/authorize"}"""),
            _ => (HttpStatusCode.OK, "{}"),
        });
        var tokenProvider = new Mock<IFhirDestinationTokenProvider>();
        var service = new FhirDestinationConnectionTestService(FactoryFor(handler), tokenProvider.Object);
        var request = new FhirConnectionTestRequest(BaseUrl, "oauth2", "cid", "csec", null, null, null);

        var result = await service.TestConnectionAsync(request, CancellationToken.None);

        result.Connected.Should().BeFalse();
        result.Error.Should().Contain("token_endpoint");
    }

    [Theory]
    [InlineData("basic")]
    [InlineData("bearer")]
    [InlineData("none")]
    public async Task Non_oauth2_auth_types_never_call_discovery(string authType)
    {
        var handler = new RoutingHandler(url => url switch
        {
            MetadataUrl => (HttpStatusCode.OK, "{}"),
            _ => (HttpStatusCode.NotFound, ""), // discovery would fail loudly if ever called
        });
        var tokenProvider = new Mock<IFhirDestinationTokenProvider>();
        var service = new FhirDestinationConnectionTestService(FactoryFor(handler), tokenProvider.Object);
        var request = new FhirConnectionTestRequest(
            BaseUrl, authType,
            ClientId: null, ClientSecret: null,
            Username: authType == "basic" ? "user" : null,
            Password: authType == "basic" ? "pass" : null,
            BearerToken: authType == "bearer" ? "token" : null);

        var result = await service.TestConnectionAsync(request, CancellationToken.None);

        result.Connected.Should().BeTrue();
        result.ResolvedTokenEndpoint.Should().BeNull();
        handler.RequestedUrls.Should().NotContain(DiscoveryUrl);
    }

    [Fact]
    public async Task Token_exchange_failure_after_successful_discovery_surfaces_as_a_connection_failure()
    {
        var handler = new RoutingHandler(url => url switch
        {
            DiscoveryUrl => (HttpStatusCode.OK, $$"""{"token_endpoint":"{{DiscoveredTokenEndpoint}}"}"""),
            _ => (HttpStatusCode.OK, "{}"),
        });
        var tokenProvider = new Mock<IFhirDestinationTokenProvider>();
        tokenProvider.Setup(p => p.GetAccessTokenAsync(It.IsAny<FhirDestinationOAuth2Options>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("invalid_client"));

        var service = new FhirDestinationConnectionTestService(FactoryFor(handler), tokenProvider.Object);
        var request = new FhirConnectionTestRequest(BaseUrl, "oauth2", "cid", "wrong-secret", null, null, null);

        var result = await service.TestConnectionAsync(request, CancellationToken.None);

        result.Connected.Should().BeFalse();
        result.Error.Should().Contain("invalid_client");
    }

    private sealed class RoutingHandler : HttpMessageHandler
    {
        private readonly Func<string, (HttpStatusCode Status, string Body)> _respond;
        public List<string> RequestedUrls { get; } = [];

        public RoutingHandler(Func<string, (HttpStatusCode Status, string Body)> respond) => _respond = respond;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            RequestedUrls.Add(url);
            var (status, body) = _respond(url);
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }
}

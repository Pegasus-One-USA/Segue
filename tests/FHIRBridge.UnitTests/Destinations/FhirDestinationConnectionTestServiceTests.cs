using System.Net;
using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.Abstractions.Security;
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
    private const string ProbeUrl = $"{BaseUrl}/Basic/fhirbridge-connection-test-probe";
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
            ProbeUrl => (HttpStatusCode.OK, ""),
            _ => (HttpStatusCode.NotFound, ""),
        });
        var tokenProvider = new Mock<IFhirDestinationTokenProvider>();
        tokenProvider.Setup(p => p.GetAccessTokenAsync(It.IsAny<FhirDestinationOAuth2Options>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("minted-token");

        var service = new FhirDestinationConnectionTestService(FactoryFor(handler), tokenProvider.Object, Mock.Of<IAzureManagedIdentityFhirTokenProvider>(), Mock.Of<IConfigurationRepository>(), Mock.Of<ISecretProvider>());
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
            ProbeUrl => (HttpStatusCode.OK, ""),
            _ => (HttpStatusCode.NotFound, ""),
        });
        var tokenProvider = new Mock<IFhirDestinationTokenProvider>();
        tokenProvider.Setup(p => p.GetAccessTokenAsync(It.IsAny<FhirDestinationOAuth2Options>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("minted-token");

        var service = new FhirDestinationConnectionTestService(FactoryFor(handler), tokenProvider.Object, Mock.Of<IAzureManagedIdentityFhirTokenProvider>(), Mock.Of<IConfigurationRepository>(), Mock.Of<ISecretProvider>());
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
        var service = new FhirDestinationConnectionTestService(FactoryFor(handler), tokenProvider.Object, Mock.Of<IAzureManagedIdentityFhirTokenProvider>(), Mock.Of<IConfigurationRepository>(), Mock.Of<ISecretProvider>());
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
        var service = new FhirDestinationConnectionTestService(FactoryFor(handler), tokenProvider.Object, Mock.Of<IAzureManagedIdentityFhirTokenProvider>(), Mock.Of<IConfigurationRepository>(), Mock.Of<ISecretProvider>());
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
            ProbeUrl => (HttpStatusCode.OK, ""),
            _ => (HttpStatusCode.NotFound, ""), // discovery would fail loudly if ever called
        });
        var tokenProvider = new Mock<IFhirDestinationTokenProvider>();
        var service = new FhirDestinationConnectionTestService(FactoryFor(handler), tokenProvider.Object, Mock.Of<IAzureManagedIdentityFhirTokenProvider>(), Mock.Of<IConfigurationRepository>(), Mock.Of<ISecretProvider>());
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
    public async Task Reusing_an_existing_destination_with_a_blank_secret_resolves_the_stored_one()
    {
        var handler = new RoutingHandler(url => url switch
        {
            DiscoveryUrl => (HttpStatusCode.OK, $$"""{"token_endpoint":"{{DiscoveredTokenEndpoint}}"}"""),
            MetadataUrl => (HttpStatusCode.OK, "{}"),
            ProbeUrl => (HttpStatusCode.OK, ""),
            _ => (HttpStatusCode.NotFound, ""),
        });
        var destinationId = Guid.NewGuid();
        var destination = new FHIRBridge.Domain.Entities.DestinationConfiguration(
            "Existing Aidbox", FHIRBridge.Domain.Enums.DestinationType.FhirRepository,
            new FHIRBridge.Domain.ValueObjects.SecretReference("kv", "secret-name"), BaseUrl);

        var configRepo = new Mock<IConfigurationRepository>();
        configRepo.Setup(r => r.GetDestinationAsync(destinationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(destination);
        var secretProvider = new Mock<ISecretProvider>();
        secretProvider.Setup(s => s.GetSecretAsync(destination.SecretReference, It.IsAny<CancellationToken>()))
            .ReturnsAsync("""{"clientId":"stored-cid","clientSecret":"stored-secret","tokenEndpoint":"https://aidbox.example.com/auth/token"}""");

        var tokenProvider = new Mock<IFhirDestinationTokenProvider>();
        tokenProvider.Setup(p => p.GetAccessTokenAsync(It.IsAny<FhirDestinationOAuth2Options>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("minted-token");

        var service = new FhirDestinationConnectionTestService(
            FactoryFor(handler), tokenProvider.Object, Mock.Of<IAzureManagedIdentityFhirTokenProvider>(),
            configRepo.Object, secretProvider.Object);
        // ClientSecret left blank ("Leave blank to keep the current client secret") — DestinationId carries
        // which saved destination's stored secret to resolve instead of failing "client secret is required".
        var request = new FhirConnectionTestRequest(
            BaseUrl, "oauth2", "cid", ClientSecret: null, null, null, null, DestinationId: destinationId);

        var result = await service.TestConnectionAsync(request, CancellationToken.None);

        result.Connected.Should().BeTrue();
        tokenProvider.Verify(p => p.GetAccessTokenAsync(
            It.Is<FhirDestinationOAuth2Options>(o => o.ClientSecret == "stored-secret"),
            It.IsAny<CancellationToken>()), Times.Once);
        // Only ClientSecret is merged in from the stored secret (see the switch in TestConnectionAsync) — the
        // stored blob's own tokenEndpoint is deliberately not consulted, so this still discovers fresh from
        // BaseUrl exactly like any other oauth2 test.
        handler.RequestedUrls.Should().Contain(DiscoveryUrl);
    }

    [Fact]
    public async Task Reusing_an_existing_destination_with_no_stored_secret_falls_back_to_the_blank_value()
    {
        var handler = new RoutingHandler(url => url switch
        {
            DiscoveryUrl => (HttpStatusCode.OK, $$"""{"token_endpoint":"{{DiscoveredTokenEndpoint}}"}"""),
            _ => (HttpStatusCode.OK, "{}"),
        });
        var destinationId = Guid.NewGuid();
        var configRepo = new Mock<IConfigurationRepository>();
        configRepo.Setup(r => r.GetDestinationAsync(destinationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((FHIRBridge.Domain.Entities.DestinationConfiguration?)null);

        var service = new FhirDestinationConnectionTestService(
            FactoryFor(handler), Mock.Of<IFhirDestinationTokenProvider>(), Mock.Of<IAzureManagedIdentityFhirTokenProvider>(),
            configRepo.Object, Mock.Of<ISecretProvider>());
        var request = new FhirConnectionTestRequest(
            BaseUrl, "oauth2", "cid", ClientSecret: null, null, null, null, DestinationId: destinationId);

        var result = await service.TestConnectionAsync(request, CancellationToken.None);

        // "Client secret is required" — same error as a genuinely new connection with no secret typed, since
        // there was nothing stored to fall back to.
        result.Connected.Should().BeFalse();
        result.Error.Should().Contain("secret");
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

        var service = new FhirDestinationConnectionTestService(FactoryFor(handler), tokenProvider.Object, Mock.Of<IAzureManagedIdentityFhirTokenProvider>(), Mock.Of<IConfigurationRepository>(), Mock.Of<ISecretProvider>());
        var request = new FhirConnectionTestRequest(BaseUrl, "oauth2", "cid", "wrong-secret", null, null, null);

        var result = await service.TestConnectionAsync(request, CancellationToken.None);

        result.Connected.Should().BeFalse();
        result.Error.Should().Contain("invalid_client");
    }

    // ── Azure FHIR Service: TenantId skips discovery; managedIdentity auth type ────────────────────────────

    [Fact]
    public async Task ClientCredentials_with_tenantId_skips_discovery_and_computes_the_entra_token_endpoint()
    {
        var handler = new RoutingHandler(url => url switch
        {
            MetadataUrl => (HttpStatusCode.OK, "{}"),
            ProbeUrl => (HttpStatusCode.OK, ""),
            _ => (HttpStatusCode.NotFound, ""), // discovery would fail loudly if ever called
        });
        var tokenProvider = new Mock<IFhirDestinationTokenProvider>();
        tokenProvider.Setup(p => p.GetAccessTokenAsync(It.IsAny<FhirDestinationOAuth2Options>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("minted-token");
        var service = new FhirDestinationConnectionTestService(FactoryFor(handler), tokenProvider.Object, Mock.Of<IAzureManagedIdentityFhirTokenProvider>(), Mock.Of<IConfigurationRepository>(), Mock.Of<ISecretProvider>());
        var request = new FhirConnectionTestRequest(
            BaseUrl, "clientCredentials", "cid", "csec", null, null, null,
            TenantId: "d5ae9301-08f4-4e80-b0b0-1b8a97b7687f");

        var result = await service.TestConnectionAsync(request, CancellationToken.None);

        result.Connected.Should().BeTrue();
        result.ResolvedTokenEndpoint.Should().Be("https://login.microsoftonline.com/d5ae9301-08f4-4e80-b0b0-1b8a97b7687f/oauth2/v2.0/token");
        handler.RequestedUrls.Should().NotContain(DiscoveryUrl);
        tokenProvider.Verify(p => p.GetAccessTokenAsync(
            It.Is<FhirDestinationOAuth2Options>(o =>
                o.TokenEndpoint == "https://login.microsoftonline.com/d5ae9301-08f4-4e80-b0b0-1b8a97b7687f/oauth2/v2.0/token"
                && o.Scope == $"{BaseUrl}/.default"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ManagedIdentity_attaches_a_token_from_the_managed_identity_provider_and_needs_no_secret()
    {
        var handler = new RoutingHandler(url => url switch
        {
            MetadataUrl => (HttpStatusCode.OK, "{}"),
            ProbeUrl => (HttpStatusCode.OK, ""),
            _ => (HttpStatusCode.NotFound, ""),
        });
        var managedIdentityProvider = new Mock<IAzureManagedIdentityFhirTokenProvider>();
        managedIdentityProvider
            .Setup(p => p.GetAccessTokenAsync($"{BaseUrl}/.default", null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync("mi-token");
        var service = new FhirDestinationConnectionTestService(FactoryFor(handler), Mock.Of<IFhirDestinationTokenProvider>(), managedIdentityProvider.Object, Mock.Of<IConfigurationRepository>(), Mock.Of<ISecretProvider>());
        var request = new FhirConnectionTestRequest(BaseUrl, "managedIdentity", null, null, null, null, null);

        var result = await service.TestConnectionAsync(request, CancellationToken.None);

        result.Connected.Should().BeTrue();
        handler.RequestedUrls.Should().NotContain(DiscoveryUrl);
    }

    // ── Write-authorization probe: GET /metadata succeeding is not proof writes are authorized ────────────

    [Fact]
    public async Task Metadata_read_succeeding_but_write_forbidden_is_reported_as_a_connection_failure()
    {
        // Reproduces the exact real-world gap this probe exists for: Azure Health Data Services (and FHIR
        // servers generally) often don't enforce write-level RBAC on GET /metadata, so a principal with no
        // write role at all could otherwise pass Test Connection and only find out at the first real pipeline
        // write. The probe below fails with 403, same as MappedFhirRepositoryDestinationWriter would.
        var handler = new RoutingHandler(url => url switch
        {
            MetadataUrl => (HttpStatusCode.OK, "{}"),
            ProbeUrl => (HttpStatusCode.Forbidden,
                """{"resourceType":"OperationOutcome","issue":[{"severity":"error","code":"forbidden","diagnostics":"Authorization failed."}]}"""),
            _ => (HttpStatusCode.NotFound, ""),
        });
        var tokenProvider = new Mock<IFhirDestinationTokenProvider>();
        tokenProvider.Setup(p => p.GetAccessTokenAsync(It.IsAny<FhirDestinationOAuth2Options>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("minted-token");
        var service = new FhirDestinationConnectionTestService(FactoryFor(handler), tokenProvider.Object, Mock.Of<IAzureManagedIdentityFhirTokenProvider>(), Mock.Of<IConfigurationRepository>(), Mock.Of<ISecretProvider>());
        var request = new FhirConnectionTestRequest(
            BaseUrl, "clientCredentials", "cid", "csec", null, null, null,
            TenantId: "d5ae9301-08f4-4e80-b0b0-1b8a97b7687f");

        var result = await service.TestConnectionAsync(request, CancellationToken.None);

        result.Connected.Should().BeFalse();
        result.Error.Should().Contain("Write check failed").And.Contain("403").And.Contain("Authorization failed");
    }

    [Fact]
    public async Task Successful_write_check_PUTs_then_DELETEs_the_probe_resource()
    {
        var handler = new RoutingHandler(url => url switch
        {
            MetadataUrl => (HttpStatusCode.OK, "{}"),
            ProbeUrl => (HttpStatusCode.OK, ""),
            _ => (HttpStatusCode.NotFound, ""),
        });
        var tokenProvider = new Mock<IFhirDestinationTokenProvider>();
        tokenProvider.Setup(p => p.GetAccessTokenAsync(It.IsAny<FhirDestinationOAuth2Options>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("minted-token");
        var service = new FhirDestinationConnectionTestService(FactoryFor(handler), tokenProvider.Object, Mock.Of<IAzureManagedIdentityFhirTokenProvider>(), Mock.Of<IConfigurationRepository>(), Mock.Of<ISecretProvider>());
        var request = new FhirConnectionTestRequest(
            BaseUrl, "clientCredentials", "cid", "csec", null, null, null,
            TenantId: "d5ae9301-08f4-4e80-b0b0-1b8a97b7687f");

        var result = await service.TestConnectionAsync(request, CancellationToken.None);

        result.Connected.Should().BeTrue();
        handler.RequestedMethods.Should().Contain((HttpMethod.Put, ProbeUrl));
        handler.RequestedMethods.Should().Contain((HttpMethod.Delete, ProbeUrl));
    }

    [Fact]
    public async Task Cleanup_delete_failure_does_not_invalidate_an_otherwise_successful_write_check()
    {
        var handler = new RoutingHandler((url, method) => (url, method) switch
        {
            (MetadataUrl, _) => (HttpStatusCode.OK, "{}"),
            (ProbeUrl, "PUT") => (HttpStatusCode.OK, ""),
            (ProbeUrl, "DELETE") => (HttpStatusCode.InternalServerError, "boom"),
            _ => (HttpStatusCode.NotFound, ""),
        });
        var tokenProvider = new Mock<IFhirDestinationTokenProvider>();
        tokenProvider.Setup(p => p.GetAccessTokenAsync(It.IsAny<FhirDestinationOAuth2Options>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("minted-token");
        var service = new FhirDestinationConnectionTestService(FactoryFor(handler), tokenProvider.Object, Mock.Of<IAzureManagedIdentityFhirTokenProvider>(), Mock.Of<IConfigurationRepository>(), Mock.Of<ISecretProvider>());
        var request = new FhirConnectionTestRequest(
            BaseUrl, "clientCredentials", "cid", "csec", null, null, null,
            TenantId: "d5ae9301-08f4-4e80-b0b0-1b8a97b7687f");

        var result = await service.TestConnectionAsync(request, CancellationToken.None);

        result.Connected.Should().BeTrue();
    }

    private sealed class RoutingHandler : HttpMessageHandler
    {
        private readonly Func<string, string, (HttpStatusCode Status, string Body)> _respond;
        public List<string> RequestedUrls { get; } = [];
        public List<(HttpMethod Method, string Url)> RequestedMethods { get; } = [];

        public RoutingHandler(Func<string, (HttpStatusCode Status, string Body)> respond)
            : this((url, _) => respond(url))
        {
        }

        public RoutingHandler(Func<string, string, (HttpStatusCode Status, string Body)> respond) => _respond = respond;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            RequestedUrls.Add(url);
            RequestedMethods.Add((request.Method, url));
            var (status, body) = _respond(url, request.Method.Method);
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }
}

using System.Net;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Domain.Enums;
using FHIRBridge.Domain.ValueObjects;
using FHIRBridge.Infrastructure.Destinations;
using FHIRBridge.Infrastructure.Destinations.Auth;
using FluentAssertions;
using Moq;

namespace FHIRBridge.UnitTests.Destinations;

public sealed class FhirRepositoryHealthCheckProviderTests
{
    private static DestinationConfiguration Destination(string? connectionMetadataJson, string? target) =>
        new("FHIR Store", DestinationType.FhirRepository, new SecretReference("kv", "secret"), target, connectionMetadataJson);

    private static (FhirRepositoryHealthCheckProvider Provider, CapturingHandler Handler) CreateProvider(
        string secretValue = "https://fhir.example.com", IFhirDestinationTokenProvider? tokenProvider = null)
    {
        var handler = new CapturingHandler(HttpStatusCode.OK);
        var httpClientFactory = new Mock<IHttpClientFactory>();
        httpClientFactory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(new HttpClient(handler));

        var secretProvider = new Mock<ISecretProvider>();
        secretProvider.Setup(s => s.GetSecretAsync(It.IsAny<SecretReference>(), It.IsAny<CancellationToken>())).ReturnsAsync(secretValue);

        var provider = new FhirRepositoryHealthCheckProvider(
            secretProvider.Object, httpClientFactory.Object, tokenProvider ?? Mock.Of<IFhirDestinationTokenProvider>());

        return (provider, handler);
    }

    [Fact]
    public void Destination_type_is_fhir_repository()
    {
        var (provider, _) = CreateProvider();
        provider.DestinationType.Should().Be(DestinationType.FhirRepository);
    }

    [Fact]
    public async Task Anonymous_check_hits_metadata_endpoint_with_no_auth_header()
    {
        var (provider, handler) = CreateProvider();
        var destination = Destination(null, "https://fhir.example.com");

        var result = await provider.CheckAsync(destination, CancellationToken.None);

        result.IsSuccessful.Should().BeTrue();
        handler.LastRequest!.RequestUri!.ToString().Should().Be("https://fhir.example.com/metadata");
        handler.LastRequest.Headers.Authorization.Should().BeNull();
    }

    [Fact]
    public async Task Bearer_check_attaches_token_from_secret()
    {
        var (provider, handler) = CreateProvider(secretValue: """{"token":"tok"}""");
        var destination = Destination("""{"dest_fhirAuthType":"bearer"}""", "https://aidbox.example.com/fhir");

        await provider.CheckAsync(destination, CancellationToken.None);

        handler.LastRequest!.Headers.Authorization!.Parameter.Should().Be("tok");
    }

    [Fact]
    public async Task Server_error_status_reports_unsuccessful()
    {
        var handler = new CapturingHandler(HttpStatusCode.ServiceUnavailable);
        var httpClientFactory = new Mock<IHttpClientFactory>();
        httpClientFactory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(new HttpClient(handler));
        var secretProvider = new Mock<ISecretProvider>();
        secretProvider.Setup(s => s.GetSecretAsync(It.IsAny<SecretReference>(), It.IsAny<CancellationToken>())).ReturnsAsync("https://fhir.example.com");
        var provider = new FhirRepositoryHealthCheckProvider(secretProvider.Object, httpClientFactory.Object, Mock.Of<IFhirDestinationTokenProvider>());

        var result = await provider.CheckAsync(Destination(null, "https://fhir.example.com"), CancellationToken.None);

        result.IsSuccessful.Should().BeFalse();
    }

    [Fact]
    public async Task Auth_enabled_without_target_reports_unsuccessful_without_throwing()
    {
        var (provider, _) = CreateProvider();
        var destination = Destination("""{"dest_fhirAuthType":"bearer"}""", target: null);

        var result = await provider.CheckAsync(destination, CancellationToken.None);

        result.IsSuccessful.Should().BeFalse();
        result.Message.Should().Contain("Target");
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _statusCode;
        public HttpRequestMessage? LastRequest { get; private set; }

        public CapturingHandler(HttpStatusCode statusCode)
        {
            _statusCode = statusCode;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(new HttpResponseMessage(_statusCode));
        }
    }
}

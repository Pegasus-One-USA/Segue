using System.Net;
using FHIRBridge.Application.Abstractions.Destinations;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Domain.Enums;
using FHIRBridge.Domain.ValueObjects;
using FHIRBridge.Infrastructure.Destinations;
using FHIRBridge.Infrastructure.Destinations.Auth;
using FluentAssertions;
using Moq;

namespace FHIRBridge.UnitTests.Destinations;

/// <summary>
/// Pins the writer's byte-for-byte behavior when <c>dest_fhirAuthType</c> is absent/"none" (every FhirRepository row
/// before auth support existed), then exercises the new opt-in bearer/clientCredentials paths.
/// </summary>
public sealed class MappedFhirRepositoryDestinationWriterTests
{
    private static DestinationConfiguration Destination(string? connectionMetadataJson, string? target = "https://fhir.example.com") =>
        new("FHIR Store", DestinationType.FhirRepository, new SecretReference("kv", "secret"), target, connectionMetadataJson);

    private static MappingProfile Mapping() =>
        new("Patient FHIR", "Patient", Guid.NewGuid(), Guid.NewGuid(), "Patient", []);

    private static MappedDestinationRecord Record(string? sourceJson = null) =>
        new(Guid.NewGuid(), "Patient", "Patient", "123", new Dictionary<string, object?>(), sourceJson);

    private static PipelineWriteContext Context() => new(true, "Workflow", DateTimeOffset.UtcNow);

    private static (MappedFhirRepositoryDestinationWriter Writer, CapturingHandler Handler, Mock<ISecretProvider> SecretProvider)
        CreateWriter(string secretValue = "https://fhir.example.com", IFhirDestinationTokenProvider? tokenProvider = null)
    {
        var handler = new CapturingHandler();
        var httpClientFactory = new Mock<IHttpClientFactory>();
        httpClientFactory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(new HttpClient(handler));

        var secretProvider = new Mock<ISecretProvider>();
        secretProvider
            .Setup(s => s.GetSecretAsync(It.IsAny<SecretReference>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(secretValue);

        var writer = new MappedFhirRepositoryDestinationWriter(
            secretProvider.Object, httpClientFactory.Object, tokenProvider ?? Mock.Of<IFhirDestinationTokenProvider>());

        return (writer, handler, secretProvider);
    }

    [Fact]
    public async Task No_metadata_sends_no_auth_header_and_resolves_target_from_secret()
    {
        var (writer, handler, secretProvider) = CreateWriter(secretValue: "https://legacy-fhir.example.com/", tokenProvider: null);
        var destination = Destination(connectionMetadataJson: null, target: null);

        await writer.WriteAsync(destination, Mapping(), [Record()], Context(), CancellationToken.None);

        handler.LastRequest!.Headers.Authorization.Should().BeNull();
        handler.LastRequest.RequestUri!.ToString().Should().StartWith("https://legacy-fhir.example.com/Patient/");
        secretProvider.Verify(s => s.GetSecretAsync(It.IsAny<SecretReference>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Explicit_none_auth_type_behaves_identically_to_absent_metadata()
    {
        var (writer, handler, _) = CreateWriter(secretValue: "https://legacy-fhir.example.com");
        var destination = Destination("""{"dest_fhirAuthType":"none"}""", target: null);

        await writer.WriteAsync(destination, Mapping(), [Record()], Context(), CancellationToken.None);

        handler.LastRequest!.Headers.Authorization.Should().BeNull();
        handler.LastRequest.RequestUri!.ToString().Should().StartWith("https://legacy-fhir.example.com/Patient/");
    }

    [Fact]
    public async Task Target_wins_over_secret_when_both_present_and_auth_is_none()
    {
        var (writer, handler, secretProvider) = CreateWriter();
        var destination = Destination(connectionMetadataJson: null, target: "https://from-target.example.com");

        await writer.WriteAsync(destination, Mapping(), [Record()], Context(), CancellationToken.None);

        handler.LastRequest!.RequestUri!.ToString().Should().StartWith("https://from-target.example.com/Patient/");
        secretProvider.Verify(s => s.GetSecretAsync(It.IsAny<SecretReference>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Bearer_auth_attaches_token_from_secret_and_uses_target_as_base_url()
    {
        var (writer, handler, _) = CreateWriter(secretValue: """{"token":"my-static-token"}""");
        var destination = Destination(
            """{"dest_fhirAuthType":"bearer"}""",
            target: "https://aidbox.example.com/fhir");

        await writer.WriteAsync(destination, Mapping(), [Record()], Context(), CancellationToken.None);

        handler.LastRequest!.Headers.Authorization.Should().NotBeNull();
        handler.LastRequest.Headers.Authorization!.Scheme.Should().Be("Bearer");
        handler.LastRequest.Headers.Authorization.Parameter.Should().Be("my-static-token");
        handler.LastRequest.RequestUri!.ToString().Should().StartWith("https://aidbox.example.com/fhir/Patient/");
    }

    [Fact]
    public async Task ClientCredentials_auth_attaches_token_from_token_provider()
    {
        var tokenProvider = new Mock<IFhirDestinationTokenProvider>();
        tokenProvider
            .Setup(t => t.GetAccessTokenAsync(It.IsAny<FhirDestinationOAuth2Options>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("access-token-123");

        var (writer, handler, _) = CreateWriter(
            secretValue: """{"clientId":"cid","clientSecret":"csecret","tokenEndpoint":"https://aidbox.example.com/auth/token"}""",
            tokenProvider: tokenProvider.Object);
        var destination = Destination(
            """{"dest_fhirAuthType":"clientCredentials"}""",
            target: "https://aidbox.example.com/fhir");

        await writer.WriteAsync(destination, Mapping(), [Record()], Context(), CancellationToken.None);

        handler.LastRequest!.Headers.Authorization!.Parameter.Should().Be("access-token-123");
        tokenProvider.Verify(
            t => t.GetAccessTokenAsync(
                It.Is<FhirDestinationOAuth2Options>(o => o.TokenEndpoint == "https://aidbox.example.com/auth/token" && o.ClientId == "cid"),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Auth_enabled_without_target_throws()
    {
        var (writer, _, _) = CreateWriter(secretValue: """{"token":"abc"}""");
        var destination = Destination("""{"dest_fhirAuthType":"bearer"}""", target: null);

        var act = () => writer.WriteAsync(destination, Mapping(), [Record()], Context(), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Source_json_is_used_verbatim_with_id_reconciled_regardless_of_auth_mode()
    {
        var (writer, handler, _) = CreateWriter();
        var destination = Destination(connectionMetadataJson: null, target: "https://fhir.example.com");
        var record = Record("""{"resourceType":"Patient","name":[{"family":"Doe"}]}""");

        await writer.WriteAsync(destination, Mapping(), [record], Context(), CancellationToken.None);

        handler.LastRequestBody.Should().Contain("\"resourceType\":\"Patient\"");
        handler.LastRequestBody.Should().Contain("\"id\":\"123\"");
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastRequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            LastRequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}

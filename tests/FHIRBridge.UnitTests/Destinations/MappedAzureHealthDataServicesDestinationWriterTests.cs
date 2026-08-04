using System.Net;
using System.Net.Http.Headers;
using FHIRBridge.Application.Abstractions.Destinations;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Domain.Enums;
using FHIRBridge.Domain.ValueObjects;
using FHIRBridge.Infrastructure.Destinations;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace FHIRBridge.UnitTests.Destinations;

/// <summary>
/// Covers the Azure Health Data Services writer's batch-Bundle submission, per-entry result mapping, and
/// 429-retry-with-backoff behavior. Auth acquisition itself is exercised through a stub token provider — see
/// <see cref="AzureHealthDataServicesTokenProviderTests"/> for credential-selection coverage.
/// </summary>
public sealed class MappedAzureHealthDataServicesDestinationWriterTests
{
    private const string FhirServiceUrl = "https://myworkspace-myfhir.fhir.azurehealthcareapis.com";

    private static DestinationConfiguration Destination(string? connectionMetadataJson = null) =>
        new("AHDS Export", DestinationType.AzureHealthDataServices, new SecretReference("kv", "secret"), FhirServiceUrl, connectionMetadataJson);

    private static MappingProfile Mapping() =>
        new("Patient AHDS", "Patient", Guid.NewGuid(), Guid.NewGuid(), "Patient", []);

    private static MappedDestinationRecord Record(string sourceResourceId, string? sourceJson = null) =>
        new(
            Guid.NewGuid(),
            "Patient",
            "Patient",
            sourceResourceId,
            new Dictionary<string, object?> { ["Name"] = "Alice" },
            sourceJson ?? $$"""{"resourceType":"Patient","id":"{{sourceResourceId}}"}""");

    private static PipelineWriteContext Context() => new(true, "AHDS Route", DateTimeOffset.UtcNow);

    private static (MappedAzureHealthDataServicesDestinationWriter Writer, RecordingHandler Handler) CreateWriter(
        string accessToken = "test-token")
    {
        var handler = new RecordingHandler();
        var httpClient = new HttpClient(handler);

        var httpClientFactory = new Mock<IHttpClientFactory>();
        httpClientFactory.Setup(f => f.CreateClient(nameof(MappedAzureHealthDataServicesDestinationWriter))).Returns(httpClient);

        var secretProvider = new Mock<ISecretProvider>();
        secretProvider.Setup(s => s.GetSecretAsync(It.IsAny<SecretReference>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("client-secret");

        var tokenProvider = new Mock<IAzureHealthDataServicesTokenProvider>();
        tokenProvider
            .Setup(t => t.GetAccessTokenAsync(It.IsAny<AzureHealthDataServicesConnectionOptions>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(accessToken);

        var writer = new MappedAzureHealthDataServicesDestinationWriter(
            secretProvider.Object,
            httpClientFactory.Object,
            tokenProvider.Object,
            NullLogger<MappedAzureHealthDataServicesDestinationWriter>.Instance);

        return (writer, handler);
    }

    [Fact]
    public async Task Empty_record_set_writes_nothing_and_sends_no_request()
    {
        var (writer, handler) = CreateWriter();

        var result = await writer.WriteAsync(Destination(), Mapping(), [], Context(), CancellationToken.None);

        result.Count.Should().Be(0);
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Submits_a_single_batch_Bundle_with_conditional_PUT_entries()
    {
        var (writer, handler) = CreateWriter();
        handler.NextResponseFactory = () => SuccessBatchResponse(2);

        await writer.WriteAsync(Destination(), Mapping(), [Record("1"), Record("2")], Context(), CancellationToken.None);

        handler.Requests.Should().HaveCount(1);
        var request = handler.Requests[0];
        request.Method.Should().Be(HttpMethod.Post);
        request.Uri.Should().Be($"{FhirServiceUrl}/");
        request.Body.Should().Contain("\"type\":\"batch\"");
        request.Body.Should().Contain("\"method\":\"PUT\"");
        request.Body.Should().Contain("Patient/1");
        request.Body.Should().Contain("Patient/2");
        request.AuthorizationHeader.Should().Be("Bearer test-token");
    }

    [Fact]
    public async Task Reports_per_entry_success_and_failure_from_the_batch_response()
    {
        var (writer, handler) = CreateWriter();
        handler.NextResponseFactory = () => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """
                {
                    "resourceType": "Bundle",
                    "type": "batch-response",
                    "entry": [
                        { "response": { "status": "201 Created" } },
                        { "response": { "status": "400 Bad Request" } }
                    ]
                }
                """),
        };

        var result = await writer.WriteAsync(
            Destination(), Mapping(), [Record("1"), Record("2")], Context(), CancellationToken.None);

        result.Count.Should().Be(1);
        result.RecordErrors.Should().ContainSingle(e => e.Contains("Patient/2"));
        result.WrittenResourceIds.Should().ContainSingle(id => id == "1");
    }

    [Fact]
    public async Task Retries_on_429_and_succeeds_on_a_later_attempt()
    {
        var (writer, handler) = CreateWriter();
        handler.QueuedResponseFactories.Enqueue(TooManyRequests);
        handler.QueuedResponseFactories.Enqueue(() => SuccessBatchResponse(1));

        var result = await writer.WriteAsync(Destination(), Mapping(), [Record("1")], Context(), CancellationToken.None);

        result.Count.Should().Be(1);
        handler.Requests.Should().HaveCount(2);
    }

    [Fact]
    public async Task Exhausting_retries_on_429_throws()
    {
        var (writer, handler) = CreateWriter();
        handler.NextResponseFactory = TooManyRequests;

        var act = async () => await writer.WriteAsync(Destination(), Mapping(), [Record("1")], Context(), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        handler.Requests.Should().HaveCount(3);
    }

    [Fact]
    public async Task Non_success_non_429_response_throws_with_the_response_body()
    {
        var (writer, handler) = CreateWriter();
        handler.NextResponseFactory = () => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("invalid_client"),
        };

        var act = async () => await writer.WriteAsync(Destination(), Mapping(), [Record("1")], Context(), CancellationToken.None);

        (await act.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*401*invalid_client*");
    }

    // Sets Retry-After to a near-zero delta so the writer's backoff formula (which would otherwise back off in
    // real seconds) doesn't slow this test down — the writer still honors whatever the server actually sent.
    private static HttpResponseMessage TooManyRequests()
    {
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMilliseconds(1));
        return response;
    }

    private static HttpResponseMessage SuccessBatchResponse(int entryCount)
    {
        var entries = string.Join(",", Enumerable.Repeat("""{ "response": { "status": "200 OK" } }""", entryCount));
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent($$"""{"resourceType":"Bundle","type":"batch-response","entry":[{{entries}}]}"""),
        };
    }

    private sealed record CapturedRequest(HttpMethod Method, string? Uri, string? Body, string? AuthorizationHeader);

    /// <summary>
    /// Each response comes from a factory rather than a shared instance, since the writer's retry loop disposes a
    /// throttled response before issuing the next attempt — reusing one instance across calls would hand back an
    /// already-disposed <see cref="HttpResponseMessage"/> on the second send.
    /// </summary>
    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<CapturedRequest> Requests { get; } = [];
        public Func<HttpResponseMessage>? NextResponseFactory { get; set; }
        public Queue<Func<HttpResponseMessage>> QueuedResponseFactories { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new CapturedRequest(
                request.Method,
                request.RequestUri?.ToString(),
                body,
                request.Headers.Authorization?.ToString()));

            if (QueuedResponseFactories.Count > 0)
            {
                return QueuedResponseFactories.Dequeue()();
            }

            return NextResponseFactory?.Invoke() ?? new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"resourceType":"Bundle","type":"batch-response","entry":[]}"""),
            };
        }
    }
}

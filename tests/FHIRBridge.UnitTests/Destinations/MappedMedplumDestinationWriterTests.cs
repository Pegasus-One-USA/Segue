using System.Net;
using System.Net.Http.Headers;
using FHIRBridge.Application.Abstractions.Destinations;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Domain.Enums;
using FHIRBridge.Domain.ValueObjects;
using FHIRBridge.Infrastructure.Destinations;
using FHIRBridge.Runtime.Application.Abstractions.Auth;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace FHIRBridge.UnitTests.Destinations;

/// <summary>
/// The Medplum writer mints an OAuth2 client_credentials bearer token, then upserts each record's FHIR resource
/// idempotently: a conditional <c>PUT {type}?identifier={system}|{value}</c> when the resource carries a business
/// identifier, otherwise a logical-id <c>PUT {type}/{id}</c>. It backs off and retries on HTTP 429, and isolates
/// per-record failures into the write result rather than failing the whole batch.
/// </summary>
public sealed class MappedMedplumDestinationWriterTests
{
    private const string BaseUrl = "https://api.medplum.example/fhir/R4";

    private static DestinationConfiguration Destination(string? connectionMetadataJson) =>
        new("Medplum", DestinationType.Medplum, new SecretReference("kv", "medplum-secret"), BaseUrl, connectionMetadataJson);

    private static MappingProfile Mapping() =>
        new("Patient → Medplum", "Patient", Guid.NewGuid(), Guid.NewGuid(), "Patient", []);

    private static PipelineWriteContext Context() =>
        new(false, "Medplum Sync", DateTimeOffset.UtcNow);

    private static MappedDestinationRecord Record(string sourceJson, string? sourceId = "src-1") =>
        new(Guid.NewGuid(), "Patient", "Patient", sourceId, new Dictionary<string, object?>(), SourceJson: sourceJson);

    private const string ClientMetadata = """{"medplumClientId":"client-123"}""";

    private static (MappedMedplumDestinationWriter Writer, RecordingHandler Handler) CreateWriter(
        Func<HttpRequestMessage, HttpResponseMessage>? fhirResponder = null,
        string secret = "the-secret")
    {
        var handler = new RecordingHandler(fhirResponder);
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient(handler));

        var secretProvider = new Mock<ISecretProvider>();
        secretProvider
            .Setup(s => s.GetSecretAsync(It.IsAny<SecretReference>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(secret);

        var tokenProvider = new MedplumTokenProvider(factory.Object, new StubJwtFactory());
        var writer = new MappedMedplumDestinationWriter(
            secretProvider.Object, factory.Object, tokenProvider, NullLogger<MappedMedplumDestinationWriter>.Instance);
        return (writer, handler);
    }

    [Fact]
    public async Task Upserts_by_identifier_when_resource_carries_one()
    {
        var (writer, handler) = CreateWriter();
        var resource = """
            {"resourceType":"Patient","id":"abc","identifier":[{"system":"http://epic/patientId","value":"P001"}]}
            """;

        var result = await writer.WriteAsync(
            Destination(ClientMetadata), Mapping(), [Record(resource)], Context(), CancellationToken.None);

        result.Count.Should().Be(1);
        var put = handler.FhirRequests.Should().ContainSingle().Subject;
        put.Method.Should().Be(HttpMethod.Put);
        // Conditional token search: identifier={system}|{value}. (.NET's Uri normalizes the encoded pipe to a
        // literal '|' in the query, which is valid and exactly what FHIR expects — assert on decoded semantics.)
        put.Url.Should().StartWith($"{BaseUrl}/Patient?identifier=");
        Uri.UnescapeDataString(put.Url).Should().Contain("http://epic/patientId|P001");
        put.AuthorizationScheme.Should().Be("Bearer");
        put.ContentType.Should().StartWith("application/fhir+json");
        // Conditional upsert drops the body id so the server owns identity.
        put.Body.Should().NotContain("\"id\"");
    }

    [Fact]
    public async Task Prefers_the_configured_identifier_system()
    {
        var (writer, handler) = CreateWriter();
        var resource = """
            {"resourceType":"Patient","identifier":[
              {"system":"http://other/mrn","value":"X9"},
              {"system":"http://epic/patientId","value":"P001"}]}
            """;
        var metadata = """{"medplumClientId":"client-123","medplumIdentifierSystem":"http://epic/patientId"}""";

        await writer.WriteAsync(Destination(metadata), Mapping(), [Record(resource)], Context(), CancellationToken.None);

        handler.FhirRequests.Single().Url.Should().Contain("P001").And.NotContain("X9");
    }

    [Fact]
    public async Task Stamps_a_synthetic_identifier_and_upserts_when_resource_has_no_identifier()
    {
        // Medplum rejects a client-chosen logical id (PUT /Type/{id} -> 400 "Invalid id"), so a resource with no
        // business identifier is upserted by a synthetic identifier derived from the source id instead.
        var (writer, handler) = CreateWriter();
        var resource = """{"resourceType":"Observation","id":"obs-7","status":"final"}""";

        await writer.WriteAsync(
            Destination(ClientMetadata), Mapping(), [Record(resource, sourceId: "src-1")], Context(), CancellationToken.None);

        var put = handler.FhirRequests.Single();
        put.Method.Should().Be(HttpMethod.Put);
        // Conditional upsert by the synthetic identifier (default urn system + the source id), NOT a logical-id PUT.
        put.Url.Should().StartWith($"{BaseUrl}/Observation?identifier=");
        Uri.UnescapeDataString(put.Url).Should().Contain("urn:fhirbridge:source-id|src-1");
        // Body carries the synthetic identifier and has the client-chosen logical id removed.
        put.Body.Should().Contain("urn:fhirbridge:source-id");
        put.Body.Should().NotContain("\"id\"");
    }

    [Fact]
    public async Task Derives_the_token_url_from_the_fhir_base_url()
    {
        var (writer, handler) = CreateWriter();
        var resource = """{"resourceType":"Patient","id":"abc"}""";

        await writer.WriteAsync(
            Destination(ClientMetadata), Mapping(), [Record(resource)], Context(), CancellationToken.None);

        handler.TokenRequests.Should().ContainSingle()
            .Which.Url.Should().Be("https://api.medplum.example/oauth2/token");
    }

    [Fact]
    public async Task Retries_on_429_then_succeeds()
    {
        var calls = 0;
        var (writer, handler) = CreateWriter(_ =>
        {
            calls++;
            if (calls == 1)
            {
                var throttled = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                throttled.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(1));
                return throttled;
            }

            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var resource = """{"resourceType":"Patient","id":"abc"}""";

        var result = await writer.WriteAsync(
            Destination(ClientMetadata), Mapping(), [Record(resource)], Context(), CancellationToken.None);

        result.Count.Should().Be(1);
        handler.FhirRequests.Should().HaveCount(2);
    }

    [Fact]
    public async Task Isolates_per_record_failures()
    {
        var (writer, handler) = CreateWriter(req =>
            new HttpResponseMessage(req.RequestUri!.ToString().Contains("bad")
                ? HttpStatusCode.BadRequest
                : HttpStatusCode.OK));

        var good = Record("""{"resourceType":"Patient","id":"good"}""", "good");
        var bad = Record("""{"resourceType":"Patient","id":"bad"}""", "bad");

        var result = await writer.WriteAsync(
            Destination(ClientMetadata), Mapping(), [good, bad], Context(), CancellationToken.None);

        result.Count.Should().Be(1);
        result.RecordErrors.Should().ContainSingle().Which.Should().Contain("bad");
        result.WrittenResourceIds.Should().ContainSingle().Which.Should().Be("good");
    }

    [Fact]
    public async Task Uses_client_secret_by_default()
    {
        var (writer, handler) = CreateWriter(secret: "sym-secret");

        await writer.WriteAsync(
            Destination(ClientMetadata), Mapping(),
            [Record("""{"resourceType":"Patient","id":"abc"}""")], Context(), CancellationToken.None);

        var tokenBody = handler.TokenRequests.Single().Body!;
        tokenBody.Should().Contain("client_secret=sym-secret");
        tokenBody.Should().NotContain("client_assertion");
    }

    [Fact]
    public async Task Uses_private_key_jwt_assertion_when_configured()
    {
        // In this mode the SecretReference holds the PEM private key; the stub JWT factory ignores it and returns a
        // fixed assertion, so any non-empty secret works here.
        var (writer, handler) = CreateWriter(secret: "-----BEGIN PRIVATE KEY-----fake-----END PRIVATE KEY-----");
        var metadata = """{"medplumClientId":"client-123","medplumAuthMethod":"private_key_jwt","medplumKeyId":"key-1"}""";

        await writer.WriteAsync(
            Destination(metadata), Mapping(),
            [Record("""{"resourceType":"Patient","id":"abc"}""")], Context(), CancellationToken.None);

        var tokenBody = handler.TokenRequests.Single().Body!;
        tokenBody.Should().Contain("client_assertion=signed-assertion");
        tokenBody.Should().Contain("client_assertion_type=urn");
        tokenBody.Should().Contain("grant_type=client_credentials");
        tokenBody.Should().NotContain("client_secret=");
    }

    // -------- async_batch mode --------

    private const string AsyncBatchMetadata = """{"medplumClientId":"client-123","medplumWriteMode":"async_batch"}""";

    private static MappedDestinationRecord IdRecord(string value, string sourceId) =>
        Record($$"""{"resourceType":"Patient","id":"{{sourceId}}","identifier":[{"system":"http://s","value":"{{value}}"}]}""", sourceId);

    private static HttpResponseMessage Ok(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body) };

    [Fact]
    public async Task Async_batch_posts_a_batch_bundle_with_respond_async_and_tallies_results()
    {
        const string batchResponse = """
            {"resourceType":"Bundle","type":"batch-response","entry":[
              {"response":{"status":"200 OK"}},
              {"response":{"status":"201 Created"}}]}
            """;
        var (writer, handler) = CreateWriter(_ => Ok(batchResponse));

        var result = await writer.WriteAsync(
            Destination(AsyncBatchMetadata), Mapping(),
            [IdRecord("1", "a"), IdRecord("2", "b")], Context(), CancellationToken.None);

        result.Count.Should().Be(2);
        result.WrittenResourceIds.Should().BeEquivalentTo(["a", "b"]);

        var post = handler.FhirRequests.Should().ContainSingle().Subject;
        post.Method.Should().Be(HttpMethod.Post);
        post.Url.Should().Be(BaseUrl);                       // posted to the FHIR base, not a resource path
        post.Prefer.Should().Be("respond-async");
        post.ContentType.Should().StartWith("application/fhir+json");
        post.Body.Should().Contain("\"type\":\"batch\"");
        post.Body.Should().Contain("\"method\":\"PUT\"");
        post.Body.Should().Contain("Patient?identifier=");
    }

    [Fact]
    public async Task Async_batch_isolates_failed_entries_by_response_status()
    {
        const string batchResponse = """
            {"resourceType":"Bundle","type":"batch-response","entry":[
              {"response":{"status":"200 OK"}},
              {"response":{"status":"412 Precondition Failed"}}]}
            """;
        var (writer, _) = CreateWriter(_ => Ok(batchResponse));

        var result = await writer.WriteAsync(
            Destination(AsyncBatchMetadata), Mapping(),
            [IdRecord("1", "a"), IdRecord("2", "b")], Context(), CancellationToken.None);

        result.Count.Should().Be(1);
        result.WrittenResourceIds.Should().BeEquivalentTo(["a"]);
        result.RecordErrors.Should().ContainSingle().Which.Should().Contain("412");
    }

    [Fact]
    public async Task Async_batch_polls_an_accepted_job_until_complete()
    {
        const string batchResponse = """
            {"resourceType":"Bundle","type":"batch-response","entry":[{"response":{"status":"200 OK"}}]}
            """;
        var (writer, handler) = CreateWriter(req =>
        {
            if (req.Method == HttpMethod.Post)
            {
                var accepted = new HttpResponseMessage(HttpStatusCode.Accepted);
                accepted.Headers.Location = new Uri($"{BaseUrl}/job/123/status");
                return accepted;
            }

            return Ok(batchResponse); // first poll completes immediately
        });

        var result = await writer.WriteAsync(
            Destination(AsyncBatchMetadata), Mapping(),
            [IdRecord("1", "a")], Context(), CancellationToken.None);

        result.Count.Should().Be(1);
        handler.FhirRequests.Should().HaveCount(2);          // POST submit + one poll GET
        handler.FhirRequests[1].Method.Should().Be(HttpMethod.Get);
    }

    [Fact]
    public async Task Async_batch_dereferences_a_completed_async_job_binary_output()
    {
        const string batchResponse = """
            {"resourceType":"Bundle","type":"batch-response","entry":[{"response":{"status":"201 Created"}}]}
            """;
        var jobDone = $$"""{"resourceType":"AsyncJob","status":"completed","output":[{"url":"{{BaseUrl}}/Binary/xyz"}]}""";
        var (writer, handler) = CreateWriter(req =>
        {
            if (req.Method == HttpMethod.Post)
            {
                var accepted = new HttpResponseMessage(HttpStatusCode.Accepted);
                accepted.Headers.Location = new Uri($"{BaseUrl}/job/123/status");
                return accepted;
            }

            return req.RequestUri!.ToString().Contains("/Binary/")
                ? Ok(batchResponse)   // Binary holds the batch-response bundle
                : Ok(jobDone);        // job status resolves to a completed AsyncJob
        });

        var result = await writer.WriteAsync(
            Destination(AsyncBatchMetadata), Mapping(),
            [IdRecord("1", "a")], Context(), CancellationToken.None);

        result.Count.Should().Be(1);
        handler.FhirRequests.Should().HaveCount(3);          // POST + job poll + Binary fetch
    }

    private sealed class StubJwtFactory : IBackendServicesJwtFactory
    {
        public string CreateClientAssertion(BackendServicesJwtRequest request) => "signed-assertion";
    }

    private sealed record CapturedRequest(
        HttpMethod Method, string Url, string? Body, string? AuthorizationScheme, string? ContentType, string? Prefer);

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage>? _fhirResponder;

        public RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage>? fhirResponder) => _fhirResponder = fhirResponder;

        public List<CapturedRequest> TokenRequests { get; } = [];
        public List<CapturedRequest> FhirRequests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);

            if (url.Contains("/oauth2/token"))
            {
                TokenRequests.Add(new CapturedRequest(request.Method, url, body, null, null, null));
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"access_token":"tok","token_type":"Bearer","expires_in":3600}""")
                };
            }

            var prefer = request.Headers.TryGetValues("Prefer", out var preferValues)
                ? string.Join(",", preferValues)
                : null;
            FhirRequests.Add(new CapturedRequest(
                request.Method, url, body,
                request.Headers.Authorization?.Scheme,
                request.Content?.Headers.ContentType?.ToString(),
                prefer));

            return _fhirResponder?.Invoke(request) ?? new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}

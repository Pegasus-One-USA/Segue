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

    private static MappedDestinationRecord Record(string? sourceJson = null, string sourceResourceId = "123", string resourceType = "Patient") =>
        new(Guid.NewGuid(), resourceType, resourceType, sourceResourceId, new Dictionary<string, object?>(), sourceJson);

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

    // ── dest_fhirWriteMode: "bundle" ─────────────────────────────────────────

    [Fact]
    public async Task Bundle_mode_sends_one_POST_to_root_with_a_batch_Bundle_body()
    {
        var (writer, handler, _) = CreateWriter();
        var destination = Destination(
            """{"dest_fhirAuthType":"none","dest_fhirWriteMode":"bundle"}""",
            target: "https://aidbox.example.com/fhir");
        var record = Record("""{"resourceType":"Patient","id":"123"}""");

        await writer.WriteAsync(destination, Mapping(), [record], Context(), CancellationToken.None);

        handler.Requests.Should().HaveCount(1);
        var (request, body) = handler.Requests[0];
        request.Method.Should().Be(HttpMethod.Post);
        request.RequestUri!.ToString().Should().Be("https://aidbox.example.com/fhir");
        body.Should().Contain("\"resourceType\":\"Bundle\"");
        body.Should().Contain("\"type\":\"batch\"");
        body.Should().Contain("\"method\":\"PUT\"");
        body.Should().Contain("\"url\":\"Patient/123\"");
    }

    [Fact]
    public async Task Bundle_mode_isolates_one_failed_entry_from_the_other_two()
    {
        var (writer, handler, _) = CreateWriter();
        handler.RespondWith = (_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""
                {
                  "resourceType": "Bundle",
                  "type": "batch-response",
                  "entry": [
                    {"response": {"status": "200 OK"}},
                    {"response": {"status": "422 Unprocessable Entity", "outcome": {"resourceType":"OperationOutcome","issue":[{"severity":"fatal","code":"invalid","diagnostics":"Referenced resource Organization/xyz does not exist"}]}}},
                    {"response": {"status": "200 OK"}}
                  ]
                }
                """)
        };
        var destination = Destination("""{"dest_fhirWriteMode":"bundle"}""", target: "https://aidbox.example.com/fhir");
        var records = new[]
        {
            Record("""{"resourceType":"Patient","id":"p1"}""", sourceResourceId: "p1"),
            Record("""{"resourceType":"Patient","id":"p2"}""", sourceResourceId: "p2"),
            Record("""{"resourceType":"Patient","id":"p3"}""", sourceResourceId: "p3"),
        };

        var result = await writer.WriteAsync(destination, Mapping(), records, Context(), CancellationToken.None);

        result.Count.Should().Be(2);
        result.WrittenResourceIds.Should().BeEquivalentTo(new[] { "p1", "p3" });
        result.RecordErrors.Should().ContainSingle();
        result.RecordErrors!.Single().Should().Be("Patient/p2: Referenced resource Organization/xyz does not exist");
    }

    [Fact]
    public async Task Bundle_mode_matches_response_entries_to_records_by_position_not_by_content()
    {
        var (writer, handler, _) = CreateWriter();
        handler.RespondWith = (_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            // Bare status entries with no resource/id info at all — proves matching is purely positional.
            Content = new StringContent("""
                {"resourceType":"Bundle","type":"batch-response","entry":[
                    {"response":{"status":"200 OK"}},
                    {"response":{"status":"200 OK"}}
                ]}
                """)
        };
        var destination = Destination("""{"dest_fhirWriteMode":"bundle"}""", target: "https://aidbox.example.com/fhir");
        var records = new[]
        {
            Record("""{"resourceType":"Patient","id":"p1"}""", sourceResourceId: "p1"),
            Record("""{"resourceType":"Patient","id":"p2"}""", sourceResourceId: "p2"),
        };

        var result = await writer.WriteAsync(destination, Mapping(), records, Context(), CancellationToken.None);

        result.WrittenResourceIds.Should().Equal("p1", "p2");
    }

    [Fact]
    public async Task Bundle_mode_writes_a_non_FHIR_fallback_record_individually_alongside_the_bundle()
    {
        var (writer, handler, _) = CreateWriter();
        var destination = Destination("""{"dest_fhirWriteMode":"bundle"}""", target: "https://aidbox.example.com/fhir");
        var records = new[]
        {
            Record("""{"resourceType":"Patient","id":"p1"}""", sourceResourceId: "p1"),
            Record(sourceJson: null, sourceResourceId: "p2"), // no SourceJson -> non-FHIR fallback path
        };

        var result = await writer.WriteAsync(destination, Mapping(), records, Context(), CancellationToken.None);

        handler.Requests.Should().HaveCount(2);
        handler.Requests[0].Body.Should().Contain("\"resourceType\":\"Bundle\"");
        handler.Requests[1].Request.Method.Should().Be(HttpMethod.Put);
        result.Count.Should().Be(2);
    }

    [Fact]
    public async Task Bundle_mode_throws_when_response_entry_count_does_not_match_request()
    {
        var (writer, handler, _) = CreateWriter();
        handler.RespondWith = (_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"resourceType":"Bundle","type":"batch-response","entry":[{"response":{"status":"200 OK"}}]}""")
        };
        var destination = Destination("""{"dest_fhirWriteMode":"bundle"}""", target: "https://aidbox.example.com/fhir");
        var records = new[]
        {
            Record("""{"resourceType":"Patient","id":"p1"}""", sourceResourceId: "p1"),
            Record("""{"resourceType":"Patient","id":"p2"}""", sourceResourceId: "p2"),
        };

        var act = () => writer.WriteAsync(destination, Mapping(), records, Context(), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    /// <summary>
    /// Captures every request sent (not just the last one) so bundle-mode tests can assert on a whole call's worth
    /// of HTTP traffic; <see cref="LastRequest"/>/<see cref="LastRequestBody"/> stay derived from the same list so
    /// every pre-existing single-request test keeps working unmodified.
    /// </summary>
    private sealed class CapturingHandler : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, string? Body)> Requests { get; } = new();

        public HttpRequestMessage? LastRequest => Requests.Count > 0 ? Requests[^1].Request : null;
        public string? LastRequestBody => Requests.Count > 0 ? Requests[^1].Body : null;

        /// <summary>Test-supplied response builder; when null, defaults to a bare 200 OK for a plain PUT, or (for a
        /// Bundle POST) an all-succeeded batch-response Bundle with one "200 OK" entry per request entry.</summary>
        public Func<HttpRequestMessage, string?, HttpResponseMessage>? RespondWith { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((request, body));

            if (RespondWith is not null)
            {
                return RespondWith(request, body);
            }

            if (body is not null && body.Contains("\"resourceType\":\"Bundle\"", StringComparison.Ordinal))
            {
                var requestEntryCount = System.Text.Json.Nodes.JsonNode.Parse(body)!["entry"]!.AsArray().Count;
                var responseEntries = string.Join(
                    ",", Enumerable.Repeat("""{"response":{"status":"200 OK"}}""", requestEntryCount));
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        $$"""{"resourceType":"Bundle","type":"batch-response","entry":[{{responseEntries}}]}""")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}

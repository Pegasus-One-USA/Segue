using System.Net;
using System.Text.Json;
using FHIRBridge.Runtime.Application.Abstractions.Auth;
using FHIRBridge.Runtime.Application.DTOs;
using FHIRBridge.Runtime.Domain.Enums;
using FHIRBridge.Runtime.Infrastructure.Connectors;
using FluentAssertions;

namespace FHIRBridge.Runtime.UnitTests.Connectors;

/// <summary>
/// The bulk-export client expresses each scope as the correct kick-off request: System/Group/all-Patient as a GET
/// against the right operation path, and a patient-id-narrowed export as a POST with a <c>Parameters</c> body (the
/// only spec-valid shape for a specific patient list). The subsequent poll/download loop is exercised by returning an
/// empty manifest, keeping these focused on the kick-off contract.
/// </summary>
public sealed class FhirBulkExportClientTests
{
    private static readonly FhirSourceConfiguration Source = new(
        RuntimeSourceType.Epic, "Epic", "https://fhir.example.com/R4", "https://auth/token", "client-1",
        null, null, [], SearchCount: 100, MaxPages: 1);

    private static FhirRestBulkExportClient CreateClient(HttpMessageHandler handler, string token = "access-token") =>
        new(new HttpClient(handler), new StubTokenProvider(token), delay: (_, _) => Task.CompletedTask);

    [Fact]
    public async Task System_scope_issues_a_get_to_the_system_export_endpoint()
    {
        var handler = new RecordingHandler();
        var client = CreateClient(handler);

        await client.ExportAsync(
            new FhirBulkExportRequest(BulkExportScope.System, ResourceTypes: ["Patient"], OutputFormat: "application/fhir+ndjson"),
            Source, CancellationToken.None);

        handler.KickOffMethod.Should().Be(HttpMethod.Get);
        handler.KickOffUri.Should().Contain("/R4/$export");
        handler.KickOffUri.Should().Contain("_type=Patient");
        handler.KickOffUri.Should().Contain("_outputFormat=");
        handler.KickOffBody.Should().BeNull();
    }

    [Fact]
    public async Task Group_scope_issues_a_get_to_the_group_export_endpoint()
    {
        var handler = new RecordingHandler();
        var client = CreateClient(handler);

        await client.ExportAsync(
            new FhirBulkExportRequest(BulkExportScope.Group, GroupId: "grp-9", ResourceTypes: ["Observation"]),
            Source, CancellationToken.None);

        handler.KickOffMethod.Should().Be(HttpMethod.Get);
        handler.KickOffUri.Should().Contain("/Group/grp-9/$export");
    }

    [Fact]
    public async Task All_patient_scope_without_ids_issues_a_get()
    {
        var handler = new RecordingHandler();
        var client = CreateClient(handler);

        await client.ExportAsync(
            new FhirBulkExportRequest(BulkExportScope.Patient, ResourceTypes: ["Patient"]),
            Source, CancellationToken.None);

        handler.KickOffMethod.Should().Be(HttpMethod.Get);
        handler.KickOffUri.Should().Contain("/R4/Patient/$export");
        handler.KickOffBody.Should().BeNull();
    }

    [Fact]
    public async Task Patient_scope_with_ids_posts_a_parameters_body_with_one_patient_entry_per_id()
    {
        var handler = new RecordingHandler();
        var client = CreateClient(handler);

        await client.ExportAsync(
            new FhirBulkExportRequest(
                BulkExportScope.Patient,
                ResourceTypes: ["Patient"],
                PatientIds: ["p1", "Patient/p2"]),
            Source, CancellationToken.None);

        handler.KickOffMethod.Should().Be(HttpMethod.Post);
        handler.KickOffUri.Should().Contain("/R4/Patient/$export");

        using var document = JsonDocument.Parse(handler.KickOffBody!);
        var root = document.RootElement;
        root.GetProperty("resourceType").GetString().Should().Be("Parameters");

        var patientRefs = root.GetProperty("parameter").EnumerateArray()
            .Where(p => p.GetProperty("name").GetString() == "patient")
            .Select(p => p.GetProperty("valueReference").GetProperty("reference").GetString())
            .ToArray();

        // A bare id is normalized to Patient/<id>; an already-qualified reference is left as-is.
        patientRefs.Should().Equal("Patient/p1", "Patient/p2");
    }

    [Fact]
    public async Task Unauthenticated_source_omits_the_authorization_header()
    {
        var handler = new RecordingHandler();
        var client = CreateClient(handler, token: "");

        await client.ExportAsync(
            new FhirBulkExportRequest(BulkExportScope.System, ResourceTypes: ["Patient"]),
            Source, CancellationToken.None);

        // A loopback/unauthenticated source (e.g. local HAPI) resolves to an empty token — no bearer must be sent.
        handler.KickOffHadAuthHeader.Should().BeFalse();
    }

    [Fact]
    public async Task KickOffExportAsync_returns_the_content_location_status_url()
    {
        var handler = new RecordingHandler();
        var client = CreateClient(handler);

        var statusUrl = await client.KickOffExportAsync(
            new FhirBulkExportRequest(BulkExportScope.System, ResourceTypes: ["Patient"]),
            Source, CancellationToken.None);

        statusUrl.Should().Be("https://fhir.example.com/status/1");
    }

    [Theory]
    // eCW rejects a duplicate kick-off with HTTP 200 + an OperationOutcome (code "duplicate" / "already in progress"),
    // not a 4xx — the status alone reads like success, so the payload must be inspected.
    [InlineData(HttpStatusCode.OK, """{"resourceType":"OperationOutcome","issue":[{"severity":"error","code":"duplicate","diagnostics":"Bulk operation is already in progress for the group"}]}""")]
    [InlineData(HttpStatusCode.TooManyRequests, """{"resourceType":"OperationOutcome","issue":[{"severity":"error","code":"duplicate"}]}""")]
    [InlineData(HttpStatusCode.OK, """{"resourceType":"OperationOutcome","issue":[{"severity":"error","code":"processing","diagnostics":"An export is already in progress."}]}""")]
    public async Task Duplicate_kickoff_throws_a_duplicate_error_even_when_status_is_200(HttpStatusCode status, string body)
    {
        var handler = new SinglePollHandler(status, manifestBody: body);
        var client = CreateClient(handler);

        var act = async () => await client.KickOffExportAsync(
            new FhirBulkExportRequest(BulkExportScope.Group, GroupId: "grp-9", ResourceTypes: ["Patient"]),
            Source, CancellationToken.None);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain("duplicate");
    }

    [Fact]
    public async Task PollOnceAsync_returns_InProgress_with_RetryAfter_on_202()
    {
        var handler = new SinglePollHandler(HttpStatusCode.Accepted, retryAfterSeconds: 7);
        var client = CreateClient(handler);

        var result = await client.PollOnceAsync("https://fhir.example.com/status/1", Source, CancellationToken.None);

        result.Status.Should().Be(BulkExportPollStatus.InProgress);
        result.RetryAfter.Should().Be(TimeSpan.FromSeconds(7));
    }

    [Fact]
    public async Task PollOnceAsync_returns_Completed_with_parsed_files_on_200()
    {
        var handler = new SinglePollHandler(HttpStatusCode.OK, manifestBody: """
            { "output": [{ "type": "Patient", "url": "https://fhir.example.com/files/1.ndjson" }] }
            """);
        var client = CreateClient(handler);

        var result = await client.PollOnceAsync("https://fhir.example.com/status/1", Source, CancellationToken.None);

        result.Status.Should().Be(BulkExportPollStatus.Completed);
        result.Files.Should().ContainSingle(f => f.Url == "https://fhir.example.com/files/1.ndjson");
    }

    [Fact]
    public async Task PollOnceAsync_returns_Failed_without_throwing_on_unexpected_status()
    {
        var handler = new SinglePollHandler(HttpStatusCode.InternalServerError);
        var client = CreateClient(handler);

        var result = await client.PollOnceAsync("https://fhir.example.com/status/1", Source, CancellationToken.None);

        result.Status.Should().Be(BulkExportPollStatus.Failed);
        result.ErrorMessage.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task DownloadResultsAsync_reacquires_the_token_once_per_file()
    {
        var handler = new NdjsonDownloadHandler();
        var tokenProvider = new CountingTokenProvider("access-token");
        var client = new FhirRestBulkExportClient(new HttpClient(handler), tokenProvider, delay: (_, _) => Task.CompletedTask);

        var files = new[]
        {
            new BulkExportFile("Patient", "https://fhir.example.com/files/1.ndjson"),
            new BulkExportFile("Observation", "https://fhir.example.com/files/2.ndjson"),
            new BulkExportFile("Condition", "https://fhir.example.com/files/3.ndjson"),
        };

        await client.DownloadResultsAsync(files, Source, CancellationToken.None);

        // The token is fetched once per file, not once for the whole loop — so a long multi-file download re-mints an
        // expiring token via the provider instead of holding one token past its lifetime and 401-ing on later files.
        tokenProvider.CallCount.Should().Be(files.Length);
    }

    [Fact]
    public async Task DownloadResultsAsync_retries_a_transient_file_failure_instead_of_failing_the_whole_download()
    {
        // First download attempt 401s (flaky eCW edge), second succeeds — the file's download must recover on retry
        // rather than throwing and restarting the entire multi-file download.
        var handler = new FlakyDownloadHandler(failFirst: 1);
        var tokenProvider = new CountingTokenProvider("access-token");
        var client = new FhirRestBulkExportClient(new HttpClient(handler), tokenProvider, delay: (_, _) => Task.CompletedTask);

        var files = new[] { new BulkExportFile("Patient", "https://fhir.example.com/files/1.ndjson") };

        var resources = await client.DownloadResultsAsync(files, Source, CancellationToken.None);

        resources.Should().ContainSingle(r => r.ResourceType == "Patient");
        tokenProvider.CallCount.Should().Be(2, "the retry re-acquires a fresh token for the second attempt");
    }

    // Fails the first `failFirst` requests with 401, then serves NDJSON — simulates an intermittently-flaky edge.
    private sealed class FlakyDownloadHandler : HttpMessageHandler
    {
        private readonly int _failFirst;
        private int _requests;

        public FlakyDownloadHandler(int failFirst) => _failFirst = failFirst;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var n = Interlocked.Increment(ref _requests);
            return Task.FromResult(n <= _failFirst
                ? new HttpResponseMessage(HttpStatusCode.Unauthorized)
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"resourceType":"Patient","id":"x"}"""),
                });
        }
    }

    private sealed class CountingTokenProvider : IFhirAccessTokenProvider
    {
        private readonly string _token;
        private int _callCount;

        public CountingTokenProvider(string token) => _token = token;

        public int CallCount => _callCount;

        public Task<string> GetAccessTokenAsync(FhirSourceConfiguration source, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _callCount);
            return Task.FromResult(_token);
        }
    }

    // Answers any download GET with a single-line NDJSON body so DownloadResultsAsync parses one resource per file.
    private sealed class NdjsonDownloadHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"resourceType":"Patient","id":"x"}"""),
            });
    }

    private sealed class SinglePollHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _statusCode;
        private readonly int? _retryAfterSeconds;
        private readonly string? _manifestBody;

        public SinglePollHandler(HttpStatusCode statusCode, int? retryAfterSeconds = null, string? manifestBody = null)
        {
            _statusCode = statusCode;
            _retryAfterSeconds = retryAfterSeconds;
            _manifestBody = manifestBody;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(_statusCode);
            if (_retryAfterSeconds is { } seconds)
            {
                response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(seconds));
            }

            if (_manifestBody is not null)
            {
                response.Content = new StringContent(_manifestBody);
            }

            return Task.FromResult(response);
        }
    }

    private sealed class StubTokenProvider : IFhirAccessTokenProvider
    {
        private readonly string _token;
        public StubTokenProvider(string token) => _token = token;

        public Task<string> GetAccessTokenAsync(FhirSourceConfiguration source, CancellationToken cancellationToken) =>
            Task.FromResult(_token);
    }

    // First request = kick-off (captured, answered 202 + Content-Location). Any later request = status poll,
    // answered 200 with an empty manifest so the export completes with zero files.
    private sealed class RecordingHandler : HttpMessageHandler
    {
        private int _requests;

        public HttpMethod? KickOffMethod { get; private set; }
        public string? KickOffUri { get; private set; }
        public string? KickOffBody { get; private set; }
        public bool KickOffHadAuthHeader { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _requests) == 1)
            {
                KickOffMethod = request.Method;
                KickOffUri = request.RequestUri?.ToString();
                KickOffHadAuthHeader = request.Headers.Authorization is not null;
                KickOffBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);

                var accepted = new HttpResponseMessage(HttpStatusCode.Accepted);
                accepted.Headers.Location = new Uri("https://fhir.example.com/status/1");
                return accepted;
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{ "output": [] }"""),
            };
        }
    }
}

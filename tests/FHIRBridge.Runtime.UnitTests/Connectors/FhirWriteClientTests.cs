using System.Net;
using System.Net.Http.Headers;
using System.Text;
using FHIRBridge.Runtime.Application.Abstractions.Auth;
using FHIRBridge.Runtime.Application.DTOs;
using FHIRBridge.Runtime.Domain.Enums;
using FHIRBridge.Runtime.Infrastructure.Connectors;
using FluentAssertions;
using Microsoft.Extensions.Options;

namespace FHIRBridge.Runtime.UnitTests.Connectors;

/// <summary>
/// The connector's write path. The property that matters most: a create is sent once. Epic files a replayed allergy,
/// problem or note a second time, so a timeout or a 5xx must come back as an unknown outcome, never be retried.
/// </summary>
public sealed class FhirWriteClientTests
{
    private static FhirSourceConfiguration Source() => new(
        RuntimeSourceType.Epic, "Epic write", "https://fhir.example.com/R4", "https://auth/token",
        // A unique client id per test keeps the connector's static per-source throttle from coupling tests.
        "client-" + Guid.NewGuid().ToString("N"), null, null, [], TimeoutSeconds: 5);

    private static EpicFhirSourceClient Client(HttpMessageHandler handler) =>
        new(new HttpClient(handler), new StubTokenProvider(), Options.Create(new EpicFhirClientOptions
        {
            MinimumMillisecondsBetweenRequestsPerSource = 0,
            BaseRetryDelayMilliseconds = 100,
            MaxRetryJitterMilliseconds = 1,
        }));

    [Fact]
    public async Task Created_with_a_relative_location_returns_the_id_and_sends_prefer()
    {
        var handler = new ScriptedHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Created);
            response.Headers.Location = new Uri("Condition/ehl2pv-5i1", UriKind.Relative);
            return response;
        });

        var result = await Client(handler).CreateAsync("Condition", "{}", returnRepresentation: false, Source(), CancellationToken.None);

        result.Kind.Should().Be(FhirWriteOutcomeKind.Created);
        result.ResourceId.Should().Be("ehl2pv-5i1");
        handler.Requests.Should().ContainSingle();
        handler.Requests[0].Method.Should().Be(HttpMethod.Post);
        handler.Requests[0].Uri.Should().Be("https://fhir.example.com/R4/Condition");
        handler.Requests[0].Prefer.Should().Be("return=minimal");
        handler.Requests[0].ContentType.Should().Be("application/fhir+json");
    }

    [Theory]
    [InlineData("https://fhir.example.com/R4/Observation/abc/_history/2", "abc", "2")]
    [InlineData("Observation/abc", "abc", null)]
    public void Location_is_parsed_relative_or_absolute(string location, string id, string? version)
    {
        var uri = new Uri(location, UriKind.RelativeOrAbsolute);

        FhirSourceConnectorBase.ParseLocation(uri, "Observation").Should().Be((id, version));
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task A_5xx_on_create_is_sent_once_and_reported_unknown(HttpStatusCode status)
    {
        var handler = new ScriptedHandler(_ => new HttpResponseMessage(status));

        var result = await Client(handler).CreateAsync("AllergyIntolerance", "{}", false, Source(), CancellationToken.None);

        handler.Requests.Should().ContainSingle();
        result.Kind.Should().Be(FhirWriteOutcomeKind.OutcomeUnknown);
    }

    [Fact]
    public async Task A_dropped_connection_on_create_is_sent_once_and_reported_unknown()
    {
        var handler = new ScriptedHandler(_ => throw new HttpRequestException("reset"));

        var result = await Client(handler).CreateAsync("AllergyIntolerance", "{}", false, Source(), CancellationToken.None);

        handler.Requests.Should().ContainSingle();
        result.Kind.Should().Be(FhirWriteOutcomeKind.OutcomeUnknown);
    }

    [Fact]
    public async Task A_timeout_on_create_is_sent_once_and_reported_unknown()
    {
        var handler = new ScriptedHandler(_ => throw new TaskCanceledException("timeout"));

        var result = await Client(handler).CreateAsync("AllergyIntolerance", "{}", false, Source(), CancellationToken.None);

        handler.Requests.Should().ContainSingle();
        result.Kind.Should().Be(FhirWriteOutcomeKind.OutcomeUnknown);
    }

    [Fact]
    public async Task A_429_is_a_refusal_before_work_so_the_create_is_retried()
    {
        var attempt = 0;
        var handler = new ScriptedHandler(_ =>
        {
            if (attempt++ == 0)
            {
                var throttled = new HttpResponseMessage((HttpStatusCode)429);
                throttled.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.Zero);
                return throttled;
            }

            var created = new HttpResponseMessage(HttpStatusCode.Created);
            created.Headers.Location = new Uri("AllergyIntolerance/a1", UriKind.Relative);
            return created;
        });

        var result = await Client(handler).CreateAsync("AllergyIntolerance", "{}", false, Source(), CancellationToken.None);

        handler.Requests.Should().HaveCount(2);
        result.Kind.Should().Be(FhirWriteOutcomeKind.Created);
    }

    [Fact]
    public async Task An_open_circuit_refuses_before_sending_so_the_create_is_a_retryable_rejection()
    {
        var handler = new ScriptedHandler(_ => throw new BrokenCircuitException());

        var result = await Client(handler).CreateAsync("AllergyIntolerance", "{}", false, Source(), CancellationToken.None);

        result.Kind.Should().Be(FhirWriteOutcomeKind.Rejected);
        result.StatusCode.Should().BeNull();
    }

    /// <summary>Stands in for Polly's exception of the same name; the connector recognises it by name.</summary>
    private sealed class BrokenCircuitException : Exception
    {
    }

    [Fact]
    public async Task A_rejection_carries_vendor_codes_and_element_paths_but_no_diagnostics()
    {
        const string outcome = """
            {"resourceType":"OperationOutcome","issue":[{"severity":"fatal","code":"value",
             "details":{"coding":[{"code":"59012"}],"text":"The supplied modifier element value is not supported"},
             "diagnostics":"Only a verificationStatus of 'provisional' is supported.","expression":["verificationstatus"]}]}
            """;
        var handler = new ScriptedHandler(_ => new HttpResponseMessage((HttpStatusCode)422)
        {
            Content = new StringContent(outcome, Encoding.UTF8, "application/fhir+json"),
        });

        var result = await Client(handler).CreateAsync("Condition", "{}", false, Source(), CancellationToken.None);

        result.Kind.Should().Be(FhirWriteOutcomeKind.Rejected);
        result.StatusCode.Should().Be(422);
        var issue = result.Issues.Should().ContainSingle().Subject;
        issue.DetailCodes.Should().Equal("59012");
        issue.Expressions.Should().Equal("verificationstatus");
        typeof(FhirOperationOutcomeIssue).GetProperty("Diagnostics").Should().BeNull();
    }

    [Fact]
    public async Task Match_sends_only_certain_matches_as_a_boolean()
    {
        var handler = new ScriptedHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"resourceType":"Bundle","entry":[]}""", Encoding.UTF8, "application/fhir+json"),
        });

        await Client(handler).MatchPatientAsync("""{"resourceType":"Patient"}""", Source(), CancellationToken.None);

        handler.Requests.Should().ContainSingle();
        handler.Requests[0].Uri.Should().EndWith("/Patient/$match");
        handler.Requests[0].Body.Should().Contain("\"valueBoolean\":true").And.NotContain("\"valueString\"");
    }

    [Fact]
    public async Task Match_is_retried_like_a_read()
    {
        var attempt = 0;
        var handler = new ScriptedHandler(_ => attempt++ == 0
            ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(NoResults) });

        var result = await Client(handler).MatchPatientAsync("""{"resourceType":"Patient"}""", Source(), CancellationToken.None);

        handler.Requests.Should().HaveCount(2);
        result.Kind.Should().Be(FhirPatientMatchKind.None);
    }

    private const string NoResults = """{"resourceType":"Bundle","entry":[{"resource":{"resourceType":"OperationOutcome","issue":[{"severity":"warning","code":"processing","details":{"coding":[{"code":"4101"}]}}]}}]}""";

    [Theory]
    // An empty answer that does not say 4101 is never read as "no such patient" (which could lead to a create).
    [InlineData(200, """{"resourceType":"Bundle","entry":[]}""", FhirPatientMatchKind.Ambiguous, null)]
    // One candidate without a score is not certain.
    [InlineData(200, """{"resourceType":"Bundle","entry":[{"resource":{"resourceType":"Patient","id":"a"}}]}""", FhirPatientMatchKind.Ambiguous, null)]
    // A certain match: one patient, score 1 (stage A, full demographics).
    [InlineData(200, """{"resourceType":"Bundle","entry":[{"resource":{"resourceType":"Patient","id":"eAB3"},"search":{"score":1}}]}""", FhirPatientMatchKind.Certain, "eAB3")]
    // 4101: no results.
    [InlineData(200, """{"resourceType":"Bundle","entry":[{"resource":{"resourceType":"OperationOutcome","issue":[{"severity":"warning","code":"processing","details":{"coding":[{"code":"4101"}]}}]}}]}""", FhirPatientMatchKind.None, null)]
    // 59013: possible matches, none certain — HTTP 400.
    [InlineData(400, """{"resourceType":"OperationOutcome","issue":[{"severity":"fatal","code":"processing","details":{"coding":[{"code":"59013"}]}}]}""", FhirPatientMatchKind.Ambiguous, null)]
    // 59102: the request itself is invalid (string flag) — a configuration problem, not a matching one.
    [InlineData(400, """{"resourceType":"OperationOutcome","issue":[{"severity":"fatal","code":"invalid","details":{"coding":[{"code":"59102"}]}}]}""", FhirPatientMatchKind.Failed, null)]
    // Two candidates are never treated as a match.
    [InlineData(200, """{"resourceType":"Bundle","entry":[{"resource":{"resourceType":"Patient","id":"a"},"search":{"score":0.9}},{"resource":{"resourceType":"Patient","id":"b"},"search":{"score":0.9}}]}""", FhirPatientMatchKind.Ambiguous, null)]
    public void Match_outcomes_follow_the_sandbox_codes(int status, string body, FhirPatientMatchKind kind, string? patientId)
    {
        var result = FhirSourceConnectorBase.InterpretMatch(status, body);

        result.Kind.Should().Be(kind);
        result.PatientId.Should().Be(patientId);
    }

    [Fact]
    public async Task Identifier_search_escapes_the_identifier_and_returns_resources()
    {
        var handler = new ScriptedHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"resourceType":"Bundle","entry":[{"resource":{"resourceType":"Patient","id":"p1"}}]}"""),
        });

        var page = await Client(handler).SearchByIdentifierAsync("Patient", "urn:oid:1.2.3", "MRN 1", Source(), CancellationToken.None);

        page.Succeeded.Should().BeTrue();
        page.Resources.Should().ContainSingle();
        handler.Requests[0].Uri.Should().Be("https://fhir.example.com/R4/Patient?identifier=urn%3Aoid%3A1.2.3%7CMRN%201");
    }

    // ---- PR #240 review ----

    private static HttpResponseMessage Bundle(string ids, string? next) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(
            "{\"resourceType\":\"Bundle\",\"link\":[" + (next is null ? "" : "{\"relation\":\"next\",\"url\":\"" + next + "\"}") + "],\"entry\":["
            + string.Join(",", ids.Split(',').Select(id => "{\"resource\":{\"resourceType\":\"Encounter\",\"id\":\"" + id + "\"}}")) + "]}"),
    };

    [Fact]
    public async Task A_patient_search_follows_next_links_so_a_later_open_encounter_is_found()
    {
        var handler = new ScriptedHandler(request => request.RequestUri!.Query.Contains("page=2", StringComparison.Ordinal)
            ? Bundle("e3", next: null)
            : Bundle("e1,e2", next: "https://fhir.example.com/R4/Encounter?patient=p1&page=2"));

        var page = await Client(handler).SearchForPatientAsync("Encounter", "p1", Source(), CancellationToken.None);

        page.Succeeded.Should().BeTrue();
        page.Resources.Should().HaveCount(3);
        handler.Requests.Should().HaveCount(2);
        handler.Requests[0].Uri.Should().Be("https://fhir.example.com/R4/Encounter?patient=p1&_count=100");
    }

    [Theory]
    [InlineData("https://elsewhere.example.org/R4/Encounter?page=2")]
    [InlineData("https://fhir.example.com/R4/Encounter?patient=p1&_count=100")]
    public async Task A_next_link_to_another_host_or_back_to_the_same_page_is_not_followed(string next)
    {
        var handler = new ScriptedHandler(_ => Bundle("e1", next));

        var page = await Client(handler).SearchForPatientAsync("Encounter", "p1", Source(), CancellationToken.None);

        page.Resources.Should().ContainSingle();
        handler.Requests.Should().ContainSingle(because: "the token is never sent to another host, and a page that repeats ends paging");
    }

    [Fact]
    public async Task A_create_cancelled_before_it_is_sent_is_a_refusal_not_an_unknown_outcome()
    {
        var handler = new ScriptedHandler(_ => new HttpResponseMessage(HttpStatusCode.Created));
        var client = new EpicFhirSourceClient(new HttpClient(handler), new CancellationAwareTokenProvider(), Options.Create(new EpicFhirClientOptions
        {
            MinimumMillisecondsBetweenRequestsPerSource = 0,
        }));
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        var result = await client.CreateAsync("AllergyIntolerance", "{}", false, Source(), cancelled.Token);

        result.Kind.Should().Be(FhirWriteOutcomeKind.Rejected);
        result.Issues.Single().DetailCodes.Should().Equal(FHIRBridge.Application.Abstractions.Destinations.EhrWriteOutcomeCodes.CancelledBeforeSend);
        handler.Requests.Should().BeEmpty();
    }

    private sealed record CapturedRequest(HttpMethod Method, string Uri, string? Prefer, string? ContentType, string? Body);

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

        public ScriptedHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        {
            _respond = respond;
        }

        public List<CapturedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new CapturedRequest(
                request.Method,
                request.RequestUri!.AbsoluteUri,
                request.Headers.TryGetValues("Prefer", out var prefer) ? prefer.Single() : null,
                request.Content?.Headers.ContentType?.MediaType,
                body));
            return _respond(request);
        }
    }

    private sealed class StubTokenProvider : IFhirAccessTokenProvider
    {
        public Task<string> GetAccessTokenAsync(FhirSourceConfiguration source, CancellationToken cancellationToken) =>
            Task.FromResult("token");
    }

    private sealed class CancellationAwareTokenProvider : IFhirAccessTokenProvider
    {
        public Task<string> GetAccessTokenAsync(FhirSourceConfiguration source, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult("token");
        }
    }
}

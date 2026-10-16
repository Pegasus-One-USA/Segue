using System.Net;
using System.Text.Json.Nodes;
using FHIRBridge.Application.Abstractions.Cds;
using FHIRBridge.Application.DTOs.Cds;
using FHIRBridge.Infrastructure.Cds;
using FluentAssertions;

namespace FHIRBridge.UnitTests.Cds;

/// <summary>
/// The FHIR callback client (Phase 2a): only the request's own fhirServer (equal to the trusted issuer's FHIR base),
/// https only unless allowed, queries kept under the base, the request's bearer token only, a deadline, bounded FHIR
/// JSON responses, never a throw for a remote failure, and the SSRF guard on the real handler. Also the admin
/// "Test callback" probe (GET /metadata, no token).
/// </summary>
public sealed class CdsFhirCallbackClientTests
{
    private const string FhirBase = "https://ehr.example.org/api/FHIR/R4";
    private const string AccessToken = "epic-short-lived-token";

    private static CdsFhirCallbackClient Client(HttpMessageHandler handler, CdsOptions? options = null) =>
        new(new HttpClient(handler, disposeHandler: false), CdsTestSupport.Options(options));

    private static CdsCallbackRequest Request(
        IReadOnlyDictionary<string, string>? queries = null,
        string fhirServer = FhirBase,
        string expected = FhirBase,
        string? token = AccessToken,
        int timeoutMs = 2000) =>
        new(
            fhirServer,
            expected,
            token is null ? null : new CdsFhirAuthorization { AccessToken = token, Subject = "cds-client" },
            queries ?? new Dictionary<string, string> { ["allergies"] = "AllergyIntolerance?patient=p-1" },
            TimeSpan.FromMilliseconds(timeoutMs));

    private static JsonObject Bundle(int entries) => new()
    {
        ["resourceType"] = "Bundle",
        ["type"] = "searchset",
        ["entry"] = new JsonArray(Enumerable.Range(0, entries)
            .Select(i => (JsonNode?)new JsonObject { ["resource"] = new JsonObject { ["resourceType"] = "AllergyIntolerance", ["id"] = $"a{i}" } })
            .ToArray()),
    };

    [Fact]
    public async Task Every_missing_key_is_fetched_in_parallel_with_the_request_token()
    {
        var http = new CdsFakeHttpHandler(request => CdsFakeHttpHandler.Json(
            request.RequestUri!.AbsolutePath.EndsWith("/Patient/p-1", StringComparison.Ordinal)
                ? new JsonObject { ["resourceType"] = "Patient", ["id"] = "p-1" }
                : Bundle(2)));
        var queries = new Dictionary<string, string>
        {
            ["allergies"] = "AllergyIntolerance?patient=p-1",
            ["patient"] = "Patient/p-1",
        };

        var result = await Client(http).FetchAsync(Request(queries), CancellationToken.None);

        result.FailureReason.Should().BeNull();
        result.ResourceCount.Should().Be(3, "a Bundle counts its entries, a single resource counts as one");
        result.Resources["patient"]!["id"]!.GetValue<string>().Should().Be("p-1");
        http.Seen.Select(s => s.Uri.ToString()).Should().BeEquivalentTo(
            $"{FhirBase}/AllergyIntolerance?patient=p-1", $"{FhirBase}/Patient/p-1");
        http.Seen.Should().AllSatisfy(seen =>
        {
            seen.Authorization.Should().Be($"Bearer {AccessToken}");
            seen.Untraced.Should().BeTrue("callback URLs hold patient ids, so they are kept out of traces");
        });
    }

    [Fact]
    public async Task An_open_server_is_called_without_an_authorization_header()
    {
        var http = new CdsFakeHttpHandler(_ => CdsFakeHttpHandler.Json(Bundle(1)));

        await Client(http).FetchAsync(Request(token: null), CancellationToken.None);

        http.Seen.Single().Authorization.Should().BeNull();
    }

    [Theory]
    [InlineData("https://attacker.example.org/api/FHIR/R4")]
    [InlineData("https://ehr.example.org/api/FHIR")]
    [InlineData("not a url")]
    public async Task A_fhir_server_other_than_the_expected_base_is_refused_without_any_request(string fhirServer)
    {
        var http = new CdsFakeHttpHandler(_ => CdsFakeHttpHandler.Json(Bundle(1)));

        var result = await Client(http).FetchAsync(Request(fhirServer: fhirServer), CancellationToken.None);

        http.Seen.Should().BeEmpty();
        result.Resources["allergies"].Should().BeNull();
        result.FailureReason.Should().Contain("not the trusted issuer's FHIR base URL");
    }

    [Fact]
    public async Task A_trailing_slash_or_host_casing_still_matches_the_expected_base()
    {
        var http = new CdsFakeHttpHandler(_ => CdsFakeHttpHandler.Json(Bundle(1)));

        var result = await Client(http).FetchAsync(Request(fhirServer: "https://EHR.example.org/api/FHIR/R4/"), CancellationToken.None);

        result.FailureReason.Should().BeNull();
        http.Seen.Should().ContainSingle();
    }

    [Fact]
    public async Task Plain_http_is_refused_unless_insecure_callbacks_are_allowed()
    {
        const string httpBase = "http://ehr.example.org/api/FHIR/R4";
        var http = new CdsFakeHttpHandler(_ => CdsFakeHttpHandler.Json(Bundle(1)));

        var refused = await Client(http).FetchAsync(Request(fhirServer: httpBase, expected: httpBase), CancellationToken.None);
        http.Seen.Should().BeEmpty("the bearer token would travel in clear text");
        refused.FailureReason.Should().Contain("not https");

        var allowed = await Client(http, new CdsOptions { AllowInsecureCallback = true })
            .FetchAsync(Request(fhirServer: httpBase, expected: httpBase), CancellationToken.None);
        allowed.FailureReason.Should().BeNull();
        http.Seen.Should().ContainSingle();
    }

    [Theory]
    [InlineData("https://attacker.example.org/steal")]
    [InlineData("//attacker.example.org/steal")]
    [InlineData("/metadata")]
    [InlineData("../../admin")]
    [InlineData("Patient/..%2F..%2Fadmin\\x")]
    [InlineData("https:attacker.example.org/x")]
    [InlineData("?_count=1")]
    [InlineData("  ")]
    public async Task A_query_that_would_leave_the_fhir_base_is_never_sent(string query)
    {
        var http = new CdsFakeHttpHandler(_ => CdsFakeHttpHandler.Json(Bundle(1)));

        var result = await Client(http).FetchAsync(
            Request(new Dictionary<string, string> { ["allergies"] = query }), CancellationToken.None);

        http.Seen.Should().BeEmpty();
        result.Resources["allergies"].Should().BeNull();
        result.FailureReason.Should().Be("query not under the FHIR base");
    }

    [Fact]
    public async Task Urls_inside_search_parameters_stay_on_the_fhir_base()
    {
        var http = new CdsFakeHttpHandler(_ => CdsFakeHttpHandler.Json(Bundle(1)));

        var result = await Client(http).FetchAsync(
            Request(new Dictionary<string, string> { ["x"] = "Observation?code=http://loinc.org|4548-4&next=https://evil.example.org/../\\" }),
            CancellationToken.None);

        result.FailureReason.Should().BeNull();
        var sent = http.Seen.Single().Uri;
        sent.Host.Should().Be("ehr.example.org");
        sent.AbsolutePath.Should().Be("/api/FHIR/R4/Observation");
    }

    public static TheoryData<string, string, string> EveryHandlerFallbackQuery()
    {
        var data = new TheoryData<string, string, string>();
        ICdsServiceHandler[] handlers =
        [
            new FHIRBridge.Infrastructure.Cds.Handlers.DrugAllergyCdsHandler(),
            new FHIRBridge.Infrastructure.Cds.Handlers.DrugInteractionCdsHandler(),
            new FHIRBridge.Infrastructure.Cds.Handlers.RenalDoseCdsHandler(),
            new FHIRBridge.Infrastructure.Cds.Handlers.CareGapCdsHandler(),
            new FHIRBridge.Infrastructure.Cds.Handlers.PriorAuthCdsHandler(),
        ];
        foreach (var handler in handlers)
        {
            foreach (var hook in handler.SupportedHooks)
            {
                foreach (var (key, template) in handler.GetFallbackQueries(hook))
                {
                    data.Add(handler.Key, key, template);
                }
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(EveryHandlerFallbackQuery))]
    public async Task Every_built_in_fallback_query_is_sent_under_the_fhir_base(string handlerKey, string key, string template)
    {
        var http = new CdsFakeHttpHandler(_ => CdsFakeHttpHandler.Json(Bundle(1)));
        var query = CdsPrefetchTemplate.Resolve(template, new JsonObject { ["patientId"] = "p/1 x" })!;

        var result = await Client(http).FetchAsync(
            Request(new Dictionary<string, string> { [key] = query }), CancellationToken.None);

        result.FailureReason.Should().BeNull($"{handlerKey}'s '{key}' query must be fetchable");
        var sent = http.Seen.Should().ContainSingle().Subject.Uri;
        sent.GetLeftPart(UriPartial.Authority).Should().Be("https://ehr.example.org");
        sent.AbsolutePath.Should().StartWith("/api/FHIR/R4/");
        sent.AbsoluteUri.Should().Contain("p%2F1%20x", "the patient id is escaped, so it cannot change the path");
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "HTTP 401")]
    [InlineData(HttpStatusCode.Forbidden, "HTTP 403")]
    [InlineData(HttpStatusCode.InternalServerError, "HTTP 500")]
    [InlineData(HttpStatusCode.Redirect, "HTTP 302")]
    public async Task An_error_status_gives_a_null_key_and_a_short_reason(HttpStatusCode status, string reason)
    {
        var http = new CdsFakeHttpHandler(_ => new HttpResponseMessage(status) { Content = new StringContent($"token {AccessToken} rejected") });

        var result = await Client(http).FetchAsync(Request(), CancellationToken.None);

        result.Resources["allergies"].Should().BeNull();
        result.FailureReason.Should().Be(reason);
    }

    [Theory]
    [InlineData("""{"resourceType":"OperationOutcome","issue":[]}""")]
    [InlineData("""{"no":"resourceType"}""")]
    [InlineData("<html>login</html>")]
    [InlineData("[1,2,3]")]
    public async Task A_body_that_is_not_a_fhir_resource_is_not_used(string body)
    {
        var http = new CdsFakeHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });

        var result = await Client(http).FetchAsync(Request(), CancellationToken.None);

        result.Resources["allergies"].Should().BeNull();
        result.FailureReason.Should().Be("not a FHIR resource");
    }

    [Fact]
    public async Task A_response_over_the_size_limit_is_not_used()
    {
        var big = Bundle(200).ToJsonString();
        var http = new CdsFakeHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(big) });

        var result = await Client(http, new CdsOptions { CallbackMaxResponseBytes = 1024 }).FetchAsync(Request(), CancellationToken.None);

        result.Resources["allergies"].Should().BeNull();
        result.FailureReason.Should().Be("response too large");
    }

    [Fact]
    public async Task A_slow_server_times_out_at_the_deadline_without_throwing()
    {
        var http = new CdsFakeHttpHandler(async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        var result = await Client(http).FetchAsync(Request(timeoutMs: 150), CancellationToken.None);

        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(3));
        result.Resources["allergies"].Should().BeNull();
        result.FailureReason.Should().Be("timed out");
    }

    [Fact]
    public async Task A_network_failure_becomes_unreachable_and_never_throws()
    {
        var http = new CdsFakeHttpHandler((_, _) => throw new HttpRequestException($"connect to {FhirBase} failed"));

        var result = await Client(http).FetchAsync(Request(), CancellationToken.None);

        result.FailureReason.Should().Be("unreachable");
    }

    [Fact]
    public async Task One_failed_key_keeps_the_others()
    {
        var http = new CdsFakeHttpHandler(request => request.RequestUri!.AbsolutePath.Contains("Patient", StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.NotFound)
            : CdsFakeHttpHandler.Json(Bundle(1)));
        var queries = new Dictionary<string, string> { ["allergies"] = "AllergyIntolerance?patient=p-1", ["patient"] = "Patient/p-1" };

        var result = await Client(http).FetchAsync(Request(queries), CancellationToken.None);

        result.Resources["allergies"].Should().NotBeNull();
        result.Resources["patient"].Should().BeNull();
        result.ResourceCount.Should().Be(1);
        result.FailureReason.Should().Be("HTTP 404");
    }

    [Fact]
    public async Task A_token_with_control_characters_is_never_sent()
    {
        var http = new CdsFakeHttpHandler(_ => CdsFakeHttpHandler.Json(Bundle(1)));

        var result = await Client(http).FetchAsync(Request(token: "abc\r\nX-Injected: 1"), CancellationToken.None);

        http.Seen.Should().BeEmpty();
        result.FailureReason.Should().Contain("token is not usable");
    }

    [Fact]
    public async Task Failure_reasons_never_carry_the_token_or_the_patient_query()
    {
        var http = new CdsFakeHttpHandler((_, _) => throw new HttpRequestException($"Bearer {AccessToken} for AllergyIntolerance?patient=p-1"));

        var result = await Client(http).FetchAsync(Request(), CancellationToken.None);

        result.FailureReason.Should().NotContain(AccessToken).And.NotContain("p-1");
    }

    [Theory]
    [InlineData("https://127.0.0.1/fhir")]
    [InlineData("https://10.0.0.5/fhir")]
    [InlineData("https://169.254.169.254/latest")]
    [InlineData("https://[::1]/fhir")]
    public async Task The_ssrf_guard_refuses_private_and_loopback_addresses(string fhirServer)
    {
        using var guarded = CdsJwksHttpHandler.CreateGuarded(() => false);
        var client = new CdsFhirCallbackClient(new HttpClient(guarded, disposeHandler: false), CdsTestSupport.Options());

        var result = await client.FetchAsync(Request(fhirServer: fhirServer, expected: fhirServer, timeoutMs: 5000), CancellationToken.None);

        result.Resources["allergies"].Should().BeNull();
        result.FailureReason.Should().Be("unreachable");
    }

    [Fact]
    public void The_ssrf_guard_does_not_follow_redirects()
    {
        using var guarded = CdsJwksHttpHandler.CreateGuarded(() => false);

        guarded.AllowAutoRedirect.Should().BeFalse();
    }

    // ---- Test callback probe ----

    [Fact]
    public async Task The_probe_reads_the_capability_statement_without_sending_any_token()
    {
        var http = new CdsFakeHttpHandler(_ => CdsFakeHttpHandler.Json(new JsonObject
        {
            ["resourceType"] = "CapabilityStatement",
            ["fhirVersion"] = "4.0.1",
            ["software"] = new JsonObject { ["name"] = "Epic", ["version"] = "November 2025" },
        }));

        var probe = await Client(http).ProbeMetadataAsync(FhirBase + "/", CancellationToken.None);

        probe.Reachable.Should().BeTrue(probe.FailureReason);
        probe.HttpStatus.Should().Be(200);
        probe.FhirVersion.Should().Be("4.0.1");
        probe.Software.Should().Be("Epic November 2025");
        var seen = http.Seen.Single();
        seen.Uri.ToString().Should().Be($"{FhirBase}/metadata");
        seen.Authorization.Should().BeNull();
    }

    [Fact]
    public async Task The_probe_reports_a_non_capability_answer_and_an_error_status()
    {
        var notFhir = await Client(new CdsFakeHttpHandler(_ => CdsFakeHttpHandler.Json(new JsonObject { ["resourceType"] = "Patient" })))
            .ProbeMetadataAsync(FhirBase, CancellationToken.None);
        var error = await Client(new CdsFakeHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)))
            .ProbeMetadataAsync(FhirBase, CancellationToken.None);

        notFhir.Reachable.Should().BeFalse();
        notFhir.FailureReason.Should().Contain("CapabilityStatement");
        error.Reachable.Should().BeFalse();
        error.HttpStatus.Should().Be(503);
    }

    [Fact]
    public async Task The_probe_refuses_plain_http_and_private_addresses()
    {
        var http = new CdsFakeHttpHandler(_ => CdsFakeHttpHandler.Json(new JsonObject { ["resourceType"] = "CapabilityStatement" }));
        var insecure = await Client(http).ProbeMetadataAsync("http://ehr.example.org/fhir", CancellationToken.None);
        http.Seen.Should().BeEmpty();
        insecure.Reachable.Should().BeFalse();
        insecure.FailureReason.Should().Contain("not https");

        using var guarded = CdsJwksHttpHandler.CreateGuarded(() => false);
        var loopback = await new CdsFhirCallbackClient(new HttpClient(guarded, disposeHandler: false), CdsTestSupport.Options())
            .ProbeMetadataAsync("https://127.0.0.1/fhir", CancellationToken.None);
        loopback.Reachable.Should().BeFalse();
        loopback.FailureReason.Should().Contain("could not be reached");
    }
}

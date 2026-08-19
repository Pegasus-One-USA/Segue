using System.Net;
using FHIRBridge.Runtime.Application.Abstractions.Auth;
using FHIRBridge.Runtime.Application.DTOs;
using FHIRBridge.Runtime.Domain.Enums;
using FHIRBridge.Runtime.Infrastructure.Connectors;
using FluentAssertions;

namespace FHIRBridge.Runtime.UnitTests.Connectors;

/// <summary>
/// When the token grant carries a launch patient context (interactive SMART), the source connector must scope the
/// fetch to that patient — the launched patient by <c>_id</c>, every other resource type by <c>patient</c>. A grant
/// with no patient context (or a caller that already pinned the patient) leaves the query untouched.
/// </summary>
public sealed class PatientScopedSearchTests
{
    private const string PatientId = "eXYZ123";
    private static readonly FhirSourceConfiguration Source = new(
        RuntimeSourceType.Epic, "Epic", "https://fhir.example.com/R4", "https://auth/token", "client-1",
        null, null, [], SearchCount: 100, MaxPages: 1);

    [Fact]
    public async Task Patient_resource_is_scoped_by_id()
    {
        var handler = new CapturingHandler();
        var client = new EpicFhirSourceClient(new HttpClient(handler), new PatientContextProvider(PatientId));

        await client.SearchAsync("Patient", Source, CancellationToken.None);

        handler.RequestUri.Should().Contain("/Patient?").And.Contain($"_id={PatientId}");
        handler.RequestUri.Should().NotContain("patient=");
    }

    [Fact]
    public async Task Non_patient_resource_is_scoped_by_patient_parameter()
    {
        var handler = new CapturingHandler();
        var client = new EpicFhirSourceClient(new HttpClient(handler), new PatientContextProvider(PatientId));

        await client.SearchAsync("Observation", Source, CancellationToken.None);

        handler.RequestUri.Should().Contain("/Observation?").And.Contain($"patient={PatientId}");
    }

    [Fact]
    public async Task Non_compartment_resource_is_left_unscoped()
    {
        // Practitioner has no "patient" search parameter in FHIR at all (it's not patient-compartment scoped) —
        // Epic rejects it outright ("Unknown parameter: PATIENT") if a request auto-adds one.
        var handler = new CapturingHandler();
        var client = new EpicFhirSourceClient(new HttpClient(handler), new PatientContextProvider(PatientId));

        await client.SearchAsync("Practitioner", Source, CancellationToken.None);

        handler.RequestUri.Should().Contain("/Practitioner?");
        handler.RequestUri.Should().NotContain("patient=");
        handler.RequestUri.Should().NotContain("_id=");
    }

    [Fact]
    public async Task Failed_patient_scoped_request_redacts_patient_identifier_from_exception()
    {
        var handler = new CapturingHandler(HttpStatusCode.BadRequest);
        var client = new EpicFhirSourceClient(new HttpClient(handler), new PatientContextProvider(PatientId));

        var act = () => client.SearchAsync("Observation", Source, CancellationToken.None);

        var exception = await act.Should().ThrowAsync<InvalidOperationException>();
        exception.Which.Message.Should().Contain("/Observation?[redacted]");
        exception.Which.Message.Should().NotContain(PatientId);
    }

    [Fact]
    public async Task No_patient_context_leaves_the_query_unscoped()
    {
        var handler = new CapturingHandler();
        var client = new EpicFhirSourceClient(new HttpClient(handler), new PatientContextProvider(null));

        await client.SearchAsync("Patient", Source, CancellationToken.None);

        handler.RequestUri.Should().Contain("/Patient?").And.NotContain("_id=").And.NotContain("patient=");
    }

    [Fact]
    public async Task Cohort_scopes_sibling_resource_search_to_discovered_patient_ids()
    {
        var cohortSource = Source with { PatientIds = ["p1", "p2", "p3"] };
        var handler = new CapturingHandler();
        var client = new EpicFhirSourceClient(new HttpClient(handler), new PatientContextProvider(null));

        await client.SearchAsync("Observation", cohortSource, CancellationToken.None);

        handler.RequestUri.Should().Contain("/Observation?").And.Contain("patient=p1,p2,p3");
    }

    [Fact]
    public async Task Cohort_scopes_the_patient_resource_itself_by_id_list()
    {
        var cohortSource = Source with { PatientIds = ["p1", "p2"] };
        var handler = new CapturingHandler();
        var client = new EpicFhirSourceClient(new HttpClient(handler), new PatientContextProvider(null));

        await client.SearchAsync("Patient", cohortSource, CancellationToken.None);

        handler.RequestUri.Should().Contain("/Patient?").And.Contain("_id=p1,p2");
    }

    [Fact]
    public async Task Cohort_leaves_a_non_compartment_resource_unscoped()
    {
        // Regression guard: a cohort (PatientIds) must be gated by isCompartmentResource exactly like a single
        // patientId is — Practitioner has no "patient" search parameter in FHIR at all.
        var cohortSource = Source with { PatientIds = ["p1", "p2"] };
        var handler = new CapturingHandler();
        var client = new EpicFhirSourceClient(new HttpClient(handler), new PatientContextProvider(null));

        await client.SearchAsync("Practitioner", cohortSource, CancellationToken.None);

        handler.RequestUri.Should().Contain("/Practitioner?");
        handler.RequestUri.Should().NotContain("patient=");
        handler.RequestUri.Should().NotContain("_id=");
    }

    [Fact]
    public async Task Target_patient_id_takes_priority_over_a_cohort()
    {
        var cohortSource = Source with { TargetPatientId = PatientId, PatientIds = ["p1", "p2"] };
        var handler = new CapturingHandler();
        var client = new EpicFhirSourceClient(new HttpClient(handler), new PatientContextProvider(null));

        await client.SearchAsync("Observation", cohortSource, CancellationToken.None);

        handler.RequestUri.Should().Contain($"patient={PatientId}").And.NotContain("p1").And.NotContain("p2");
    }

    [Fact]
    public async Task Practitioner_search_ignores_request_time_criteria_like_every_other_non_compartment_type()
    {
        // Practitioner is outside the patient compartment (never patient-scoped) — it used to be a narrow, one-off
        // exception honoring request-time PatientSearchCriteria, but nothing in the codebase actually relied on
        // that (no caller ever sets PatientSearchCriteria specifically to search Practitioner). SourceNodeExecutors
        // clears PatientSearchCriteria before fetching a non-compartment type directly anyway, so this connector-
        // level test still applies: Practitioner behaves like every other non-compartment type at this layer.
        var source = Source with { PatientSearchCriteria = "_id=ePractA,ePractB" };
        var handler = new CapturingHandler();
        var client = new EpicFhirSourceClient(new HttpClient(handler), new PatientContextProvider(null));

        await client.SearchAsync("Practitioner", source, CancellationToken.None);

        handler.RequestUri.Should().Contain("/Practitioner?").And.NotContain("_id=ePractA,ePractB");
    }

    [Fact]
    public async Task Other_non_compartment_resource_still_ignores_request_time_criteria()
    {
        // The criteria pass-through is deliberately Practitioner-only for now — a different non-compartment type
        // must keep the original "static SearchParameters only" behavior, so request-time criteria is NOT applied.
        var source = Source with { PatientSearchCriteria = "_id=oOrg1" };
        var handler = new CapturingHandler();
        var client = new EpicFhirSourceClient(new HttpClient(handler), new PatientContextProvider(null));

        await client.SearchAsync("Organization", source, CancellationToken.None);

        handler.RequestUri.Should().Contain("/Organization?").And.NotContain("_id=oOrg1");
    }

    // Access-token provider that also advertises a (possibly absent) launch patient context.
    private sealed class PatientContextProvider : IFhirAccessTokenProvider, IFhirPatientContextProvider
    {
        private readonly string? _patientId;
        public PatientContextProvider(string? patientId) => _patientId = patientId;

        public Task<string> GetAccessTokenAsync(FhirSourceConfiguration source, CancellationToken cancellationToken) =>
            Task.FromResult("access-token");

        public Task<string?> GetPatientContextAsync(FhirSourceConfiguration source, CancellationToken cancellationToken) =>
            Task.FromResult(_patientId);

        public Task<string?> GetResolvedBaseUrlAsync(FhirSourceConfiguration source, CancellationToken cancellationToken) =>
            Task.FromResult<string?>(null);

        public Task DiscardTokenAsync(FhirSourceConfiguration source, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _statusCode;

        public CapturingHandler(HttpStatusCode statusCode = HttpStatusCode.OK)
        {
            _statusCode = statusCode;
        }

        public string? RequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri?.ToString();
            return Task.FromResult(new HttpResponseMessage(_statusCode)
            {
                Content = new StringContent(_statusCode == HttpStatusCode.OK
                    ? """{ "resourceType": "Bundle", "type": "searchset", "entry": [] }"""
                    : $$"""{ "issue": [{ "diagnostics": "patient={{PatientId}} was rejected" }] }""")
            });
        }
    }
}

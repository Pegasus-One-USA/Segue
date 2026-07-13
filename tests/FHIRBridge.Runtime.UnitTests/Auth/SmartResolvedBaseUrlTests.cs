using System.Net;
using FHIRBridge.Runtime.Application.DTOs;
using FHIRBridge.Runtime.Domain.Enums;
using FHIRBridge.Runtime.Infrastructure.Auth;
using FluentAssertions;

namespace FHIRBridge.Runtime.UnitTests.Auth;

/// <summary>
/// Covers persisting and reading back the FHIR base URL a Standalone/EhrLaunch/Patient launch actually resolved to
/// (the source connection's own configured base URL, or a hospital/organization EhrEndpoint override) — captured
/// alongside the token so a later, separately triggered workflow run can reuse the same endpoint without being told
/// which hospital again.
/// </summary>
public sealed class SmartResolvedBaseUrlTests
{
    private static readonly Guid SourceId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private static FhirSourceConfiguration Source(string? baseUrl, string? targetPatientId = null) => new(
        RuntimeSourceType.Epic, "Epic Standalone", baseUrl, "https://auth.example.com/token", "client-1",
        null, null, [], SourceConnectionId: SourceId, TargetPatientId: targetPatientId);

    [Fact]
    public async Task Resolved_base_url_is_captured_at_exchange_time_and_read_back()
    {
        var store = new InMemoryFhirAuthorizationCodeTokenStore();
        var provider = new SmartAuthorizationCodeTokenProvider(new HttpClient(new TokenHandler()), store);

        // Simulates StartStandaloneCoreAsync having already overridden BaseUrl to the selected hospital endpoint
        // before the exchange runs.
        await provider.ExchangeAuthorizationCodeAsync(
            Source("https://hospital.example.com/fhir/R4"), "auth-code", "verifier", "https://app/callback", CancellationToken.None);

        var resolvedBaseUrl = await provider.GetResolvedBaseUrlAsync(Source(baseUrl: null), CancellationToken.None);

        resolvedBaseUrl.Should().Be("https://hospital.example.com/fhir/R4");
    }

    [Fact]
    public async Task Resolved_base_url_is_isolated_per_patient_alongside_the_token()
    {
        var store = new InMemoryFhirAuthorizationCodeTokenStore();
        var provider = new SmartAuthorizationCodeTokenProvider(new HttpClient(new PatientAwareTokenHandler()), store);

        await provider.ExchangeAuthorizationCodeAsync(
            Source("https://hospital-a.example.com/fhir/R4"), "code-for-patient-a", "verifier", "https://app/callback", CancellationToken.None);
        await provider.ExchangeAuthorizationCodeAsync(
            Source("https://hospital-b.example.com/fhir/R4"), "code-for-patient-b", "verifier", "https://app/callback", CancellationToken.None);

        var patientAUrl = await provider.GetResolvedBaseUrlAsync(Source(baseUrl: null, targetPatientId: "patient-a"), CancellationToken.None);
        var patientBUrl = await provider.GetResolvedBaseUrlAsync(Source(baseUrl: null, targetPatientId: "patient-b"), CancellationToken.None);

        patientAUrl.Should().Be("https://hospital-a.example.com/fhir/R4");
        patientBUrl.Should().Be("https://hospital-b.example.com/fhir/R4");
    }

    private sealed class TokenHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{ "access_token": "token", "expires_in": 300 }""")
            });
    }

    // Returns a distinct access token + patient id per authorization code, simulating two different patients'
    // launches — each against a different hospital endpoint — against the same source connection.
    private sealed class PatientAwareTokenHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            var patient = body.Contains("code-for-patient-a") ? "patient-a" : "patient-b";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($$"""{ "access_token": "token-{{patient}}", "expires_in": 300, "patient": "{{patient}}" }""")
            };
        }
    }
}

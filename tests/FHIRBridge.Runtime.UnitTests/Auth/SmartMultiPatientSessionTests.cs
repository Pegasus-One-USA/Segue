using System.Net;
using FHIRBridge.Runtime.Application.DTOs;
using FHIRBridge.Runtime.Domain.Enums;
using FHIRBridge.Runtime.Infrastructure.Auth;
using FluentAssertions;

namespace FHIRBridge.Runtime.UnitTests.Auth;

/// <summary>
/// Covers the gap where two different patients launching against the SAME source connection would silently
/// overwrite each other's stored token/patient-context in one shared cache slot. The token store now keys each
/// session by (source connection, patient) in addition to an unscoped "default" slot, so a caller that knows which
/// patient it needs (via FhirSourceConfiguration.TargetPatientId) gets that patient's session back regardless of
/// who has logged in since — while a caller that doesn't specify one keeps the pre-existing "most recent login"
/// behavior via the "default" slot.
/// </summary>
public sealed class SmartMultiPatientSessionTests
{
    private static readonly Guid SourceId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static FhirSourceConfiguration Source(string? targetPatientId = null) => new(
        RuntimeSourceType.Epic, "Epic Standalone", null, "https://auth.example.com/token", "client-1",
        null, null, [], SourceConnectionId: SourceId, TargetPatientId: targetPatientId);

    [Fact]
    public async Task Second_patients_login_does_not_overwrite_the_first_patients_dedicated_slot()
    {
        var store = new InMemoryFhirAuthorizationCodeTokenStore();
        var provider = new SmartAuthorizationCodeTokenProvider(new HttpClient(new PatientTokenHandler()), store);

        await provider.ExchangeAuthorizationCodeAsync(Source(), "code-for-patient-a", "verifier", "https://app/callback", CancellationToken.None);
        await provider.ExchangeAuthorizationCodeAsync(Source(), "code-for-patient-b", "verifier", "https://app/callback", CancellationToken.None);

        var patientAToken = await provider.GetAccessTokenAsync(Source("patient-a"), CancellationToken.None);
        var patientBToken = await provider.GetAccessTokenAsync(Source("patient-b"), CancellationToken.None);

        patientAToken.Should().Be("token-for-patient-a");
        patientBToken.Should().Be("token-for-patient-b");
    }

    [Fact]
    public async Task Patient_context_is_isolated_per_patient_alongside_the_token()
    {
        var store = new InMemoryFhirAuthorizationCodeTokenStore();
        var provider = new SmartAuthorizationCodeTokenProvider(new HttpClient(new PatientTokenHandler()), store);

        await provider.ExchangeAuthorizationCodeAsync(Source(), "code-for-patient-a", "verifier", "https://app/callback", CancellationToken.None);
        await provider.ExchangeAuthorizationCodeAsync(Source(), "code-for-patient-b", "verifier", "https://app/callback", CancellationToken.None);

        var patientAContext = await provider.GetPatientContextAsync(Source("patient-a"), CancellationToken.None);
        var patientBContext = await provider.GetPatientContextAsync(Source("patient-b"), CancellationToken.None);

        patientAContext.Should().Be("patient-a");
        patientBContext.Should().Be("patient-b");
    }

    [Fact]
    public async Task Caller_that_does_not_specify_a_patient_gets_the_most_recent_login_unchanged_from_today()
    {
        var store = new InMemoryFhirAuthorizationCodeTokenStore();
        var provider = new SmartAuthorizationCodeTokenProvider(new HttpClient(new PatientTokenHandler()), store);

        await provider.ExchangeAuthorizationCodeAsync(Source(), "code-for-patient-a", "verifier", "https://app/callback", CancellationToken.None);
        await provider.ExchangeAuthorizationCodeAsync(Source(), "code-for-patient-b", "verifier", "https://app/callback", CancellationToken.None);

        var defaultToken = await provider.GetAccessTokenAsync(Source(targetPatientId: null), CancellationToken.None);

        defaultToken.Should().Be("token-for-patient-b");
    }

    // Returns a distinct access token + patient id per authorization code, simulating two different patients'
    // launches against the same source connection.
    private sealed class PatientTokenHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            var patient = body.Contains("code-for-patient-a") ? "patient-a" : "patient-b";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($$"""{ "access_token": "token-for-{{patient}}", "expires_in": 300, "patient": "{{patient}}" }""")
            };
        }
    }
}

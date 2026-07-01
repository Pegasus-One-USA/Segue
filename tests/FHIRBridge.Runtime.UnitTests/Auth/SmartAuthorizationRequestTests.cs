using System.Net;
using FHIRBridge.Runtime.Application.Abstractions.Auth;
using FHIRBridge.Runtime.Application.DTOs;
using FHIRBridge.Runtime.Domain.Enums;
using FHIRBridge.Runtime.Infrastructure.Auth;
using FluentAssertions;

namespace FHIRBridge.Runtime.UnitTests.Auth;

public sealed class SmartAuthorizationRequestTests
{
    private static SmartAuthorizationCodeTokenProvider Provider() =>
        new(new HttpClient(new NoopHandler()), new InMemoryFhirAuthorizationCodeTokenStore());

    private static FhirSourceConfiguration Source() => new(
        RuntimeSourceType.Epic,
        "Epic",
        "https://fhir.example.com",
        "https://auth.example.com/token",
        "client-1",
        null,
        null,
        ["user/Patient.read"],
        AuthorizationEndpoint: "https://auth.example.com/authorize");

    [Fact]
    public void Authorize_request_includes_aud_equal_to_the_fhir_base_url_and_pkce()
    {
        var request = Provider().BuildAuthorizationRequest(Source(), "https://app/callback", "state-1");

        var decoded = Uri.UnescapeDataString(request.AuthorizationUrl);
        decoded.Should().Contain("aud=https://fhir.example.com");
        request.AuthorizationUrl.Should().Contain("code_challenge_method=S256");
    }

    [Fact]
    public void Ehr_launch_request_includes_the_launch_token_and_launch_scope()
    {
        var request = Provider().BuildAuthorizationRequest(Source(), "https://app/callback", "state-1", launch: "opaque-launch");

        request.AuthorizationUrl.Should().Contain("launch=opaque-launch");
        request.AuthorizationUrl.Should().Contain("scope=launch");
    }

    private sealed class NoopHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
    }
}

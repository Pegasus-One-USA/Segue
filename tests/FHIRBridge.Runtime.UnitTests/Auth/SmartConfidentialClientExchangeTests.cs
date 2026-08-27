using System.Net;
using FHIRBridge.Runtime.Application.Abstractions.Auth;
using FHIRBridge.Runtime.Application.DTOs;
using FHIRBridge.Runtime.Domain.Enums;
using FHIRBridge.Runtime.Infrastructure.Auth;
using FluentAssertions;

namespace FHIRBridge.Runtime.UnitTests.Auth;

public sealed class SmartConfidentialClientExchangeTests
{
    [Fact]
    public async Task Confidential_symmetric_client_authenticates_with_client_secret_post()
    {
        var handler = new CapturingHandler();
        var provider = new SmartAuthorizationCodeTokenProvider(
            new HttpClient(handler), new InMemoryFhirAuthorizationCodeTokenStore());
        var source = new FhirSourceConfiguration(
            RuntimeSourceType.Epic, "Epic", null, "https://auth.example.com/token", "client-1", null, null, [],
            ClientSecret: "the-secret");

        await provider.ExchangeAuthorizationCodeAsync(source, "auth-code", "verifier", "https://app/callback", CancellationToken.None);

        handler.Body.Should().Contain("client_secret=the-secret");
        handler.Body.Should().Contain("code_verifier=verifier");
    }

    [Fact]
    public async Task Confidential_asymmetric_client_authenticates_with_private_key_jwt()
    {
        var handler = new CapturingHandler();
        var provider = new SmartAuthorizationCodeTokenProvider(
            new HttpClient(handler), new InMemoryFhirAuthorizationCodeTokenStore(), new FakeJwtFactory());
        var source = new FhirSourceConfiguration(
            RuntimeSourceType.Epic, "Epic", null, "https://auth.example.com/token", "client-1", "key-1",
            "-----BEGIN PRIVATE KEY-----abc-----END PRIVATE KEY-----", []);

        await provider.ExchangeAuthorizationCodeAsync(source, "auth-code", "verifier", "https://app/callback", CancellationToken.None);

        handler.Body.Should().Contain("client_assertion=signed-assertion");
        handler.Body.Should().Contain("client_assertion_type=urn");
    }

    [Fact]
    public async Task Public_client_sends_no_secret_or_assertion()
    {
        var handler = new CapturingHandler();
        var provider = new SmartAuthorizationCodeTokenProvider(
            new HttpClient(handler), new InMemoryFhirAuthorizationCodeTokenStore());
        var source = new FhirSourceConfiguration(
            RuntimeSourceType.Epic, "Epic", null, "https://auth.example.com/token", "client-1", null, null, []);

        await provider.ExchangeAuthorizationCodeAsync(source, "auth-code", "verifier", "https://app/callback", CancellationToken.None);

        handler.Body.Should().NotContain("client_secret");
        handler.Body.Should().NotContain("client_assertion");
    }

    private sealed class FakeJwtFactory : IBackendServicesJwtFactory
    {
        public string CreateClientAssertion(BackendServicesJwtRequest request) => "signed-assertion";
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{ "access_token": "token", "expires_in": 300 }""")
            };
        }
    }
}

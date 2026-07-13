using System.Net;
using FHIRBridge.Runtime.Application.DTOs;
using FHIRBridge.Runtime.Domain.Enums;
using FHIRBridge.Runtime.Infrastructure.Auth;
using FHIRBridge.SharedKernel.Enums;
using FluentAssertions;

namespace FHIRBridge.Runtime.UnitTests.Auth;

/// <summary>
/// Covers the interactive (EHR-launch / standalone / patient) read-back path where the token was minted at sign-in
/// and the source connection has no configured token endpoint (interactive sources discover it at launch). A pipeline
/// run right after a launch must return the still-valid cached token without needing an endpoint, and a later refresh
/// must fall back to the token endpoint stashed with the token.
/// </summary>
public sealed class SmartInteractiveCachedTokenTests
{
    private static readonly Guid SourceId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static FhirSourceConfiguration InteractiveSource() => new(
        RuntimeSourceType.Epic, "Epic EHR Launch", "https://fhir.example.com", TokenEndpoint: null, "client-1",
        null, null, [], SourceConnectionId: SourceId, ApplicationType: ApplicationType.EhrLaunch);

    [Fact]
    public async Task Valid_cached_token_is_returned_without_a_token_endpoint()
    {
        var store = new InMemoryFhirAuthorizationCodeTokenStore();
        await store.SaveAsync(
            $"smart|{SourceId}|default",
            new StoredOAuthToken("cached-access-token", null, DateTimeOffset.UtcNow.AddMinutes(30)),
            CancellationToken.None);
        var provider = new SmartAuthorizationCodeTokenProvider(new HttpClient(new ThrowingHandler()), store);

        var token = await provider.GetAccessTokenAsync(InteractiveSource(), CancellationToken.None);

        token.Should().Be("cached-access-token");
    }

    [Fact]
    public async Task Expired_token_refreshes_using_the_stashed_token_endpoint()
    {
        var handler = new CapturingHandler();
        var store = new InMemoryFhirAuthorizationCodeTokenStore();
        await store.SaveAsync(
            $"smart|{SourceId}|default",
            new StoredOAuthToken(
                "expired", "refresh-1", DateTimeOffset.UtcNow.AddMinutes(-5),
                TokenEndpoint: "https://auth.example.com/token"),
            CancellationToken.None);
        var provider = new SmartAuthorizationCodeTokenProvider(new HttpClient(handler), store);

        var token = await provider.GetAccessTokenAsync(InteractiveSource(), CancellationToken.None);

        token.Should().Be("refreshed-token");
        handler.Uri.Should().Be("https://auth.example.com/token");
        handler.Body.Should().Contain("grant_type=refresh_token");
    }

    [Fact]
    public async Task Expired_token_without_any_token_endpoint_throws()
    {
        var store = new InMemoryFhirAuthorizationCodeTokenStore();
        await store.SaveAsync(
            $"smart|{SourceId}|default",
            new StoredOAuthToken("expired", "refresh-1", DateTimeOffset.UtcNow.AddMinutes(-5)),
            CancellationToken.None);
        var provider = new SmartAuthorizationCodeTokenProvider(new HttpClient(new ThrowingHandler()), store);

        var act = () => provider.GetAccessTokenAsync(InteractiveSource(), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*no token endpoint*");
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public string? Uri { get; private set; }
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Uri = request.RequestUri?.ToString();
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{ "access_token": "refreshed-token", "expires_in": 300 }""")
            };
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("No HTTP call should be made for a valid cached token.");
    }
}

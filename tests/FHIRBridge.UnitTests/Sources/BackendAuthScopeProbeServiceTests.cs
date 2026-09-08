using System.Net;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Domain.ValueObjects;
using FHIRBridge.Infrastructure.Sources;
using FHIRBridge.Runtime.Application.Abstractions.Auth;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace FHIRBridge.UnitTests.Sources;

/// <summary>
/// Covers the "Test Connection and Next" gate's real credential exchange for both Backend System auth methods —
/// see BackendAuthScopeProbeService's remarks for why "secret" travels the raw client secret directly (nothing has
/// provisioned it into the secret store yet at this point in the wizard) while "jwt" stays vault-reference-only.
/// </summary>
public sealed class BackendAuthScopeProbeServiceTests
{
    private static Mock<IHttpClientFactory> HttpClientFactory(CapturingHandler handler)
    {
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient(handler));
        return factory;
    }

    private static BackendAuthScopeProbeService CreateService(
        CapturingHandler handler,
        Mock<ISecretProvider>? secretProvider = null,
        Mock<IBackendServicesJwtFactory>? jwtFactory = null)
    {
        secretProvider ??= new Mock<ISecretProvider>(MockBehavior.Strict);
        jwtFactory ??= new Mock<IBackendServicesJwtFactory>(MockBehavior.Strict);
        return new BackendAuthScopeProbeService(
            secretProvider.Object,
            jwtFactory.Object,
            HttpClientFactory(handler).Object,
            NullLogger<BackendAuthScopeProbeService>.Instance);
    }

    [Fact]
    public async Task Secret_method_with_post_placement_sends_client_id_and_secret_in_the_form_body()
    {
        var handler = new CapturingHandler();
        var service = CreateService(handler);
        var request = new BackendAuthScopesRequest(
            "https://athenahealth.example/oauth2/token", "cid", "secret", null, null, null, "s3cr3t", "post", null);

        var result = await service.ProbeGrantedScopesAsync(request, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.GrantedScopes.Should().BeEquivalentTo(["system/Patient.read"]);
        handler.Body.Should().Contain("grant_type=client_credentials");
        handler.Body.Should().Contain("client_id=cid");
        handler.Body.Should().Contain("client_secret=s3cr3t");
        handler.AuthorizationHeader.Should().BeNull();
    }

    [Fact]
    public async Task Secret_method_with_basic_placement_sends_authorization_header_and_omits_the_secret_from_the_body()
    {
        var handler = new CapturingHandler();
        var service = CreateService(handler);
        var request = new BackendAuthScopesRequest(
            "https://okta.example/oauth2/token", "0oaClientId", "secret", null, null, null, "s3cr3t", "basic", null);

        var result = await service.ProbeGrantedScopesAsync(request, CancellationToken.None);

        result.Success.Should().BeTrue();
        handler.AuthorizationHeader.Should().NotBeNull();
        handler.AuthorizationHeader!.Scheme.Should().Be("Basic");
        handler.AuthorizationHeader.Parameter.Should().Be(
            Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("0oaClientId:s3cr3t")));
        handler.Body.Should().NotContain("client_secret");
        handler.Body.Should().NotContain("client_id");
    }

    [Fact]
    public async Task Secret_method_defaults_to_post_placement_when_authPlacement_is_not_supplied()
    {
        var handler = new CapturingHandler();
        var service = CreateService(handler);
        var request = new BackendAuthScopesRequest(
            "https://athenahealth.example/oauth2/token", "cid", "secret", null, null, null, "s3cr3t", null, null);

        await service.ProbeGrantedScopesAsync(request, CancellationToken.None);

        handler.AuthorizationHeader.Should().BeNull();
        handler.Body.Should().Contain("client_secret=s3cr3t");
    }

    [Fact]
    public async Task Secret_method_rejected_credentials_return_a_failure_result_not_an_exception()
    {
        var handler = new CapturingHandler(HttpStatusCode.Unauthorized, """{"error":"invalid_client"}""");
        var service = CreateService(handler);
        var request = new BackendAuthScopesRequest(
            "https://athenahealth.example/oauth2/token", "cid", "secret", null, null, null, "wrong", "post", null);

        var result = await service.ProbeGrantedScopesAsync(request, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.GrantedScopes.Should().BeEmpty();
        result.Error.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Jwt_method_still_signs_a_client_assertion_and_sends_it_unchanged()
    {
        var handler = new CapturingHandler();
        var secretProvider = new Mock<ISecretProvider>();
        secretProvider
            .Setup(s => s.GetSecretAsync(
                It.Is<SecretReference>(r => r.KeyVaultName == "vault" && r.SecretName == "secret-name"),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync("-----BEGIN PRIVATE KEY-----\nfake\n-----END PRIVATE KEY-----");
        var jwtFactory = new Mock<IBackendServicesJwtFactory>();
        jwtFactory.Setup(j => j.CreateClientAssertion(It.IsAny<BackendServicesJwtRequest>())).Returns("signed-jwt");

        var service = CreateService(handler, secretProvider, jwtFactory);
        var request = new BackendAuthScopesRequest(
            "https://fhir.epic.com/oauth2/token", "cid", "jwt", "kid-1", "vault", "secret-name", null, null, null);

        var result = await service.ProbeGrantedScopesAsync(request, CancellationToken.None);

        result.Success.Should().BeTrue();
        handler.Body.Should().Contain("client_assertion=signed-jwt");
        handler.Body.Should().Contain("client_assertion_type=urn%3Aietf%3Aparams%3Aoauth%3Aclient-assertion-type%3Ajwt-bearer");
        handler.Body.Should().NotContain("client_secret");
        handler.AuthorizationHeader.Should().BeNull();
    }

    [Fact]
    public async Task Missing_authMethod_falls_back_to_jwt_for_backward_compatibility()
    {
        var handler = new CapturingHandler();
        var secretProvider = new Mock<ISecretProvider>();
        secretProvider
            .Setup(s => s.GetSecretAsync(It.IsAny<SecretReference>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("pem");
        var jwtFactory = new Mock<IBackendServicesJwtFactory>();
        jwtFactory.Setup(j => j.CreateClientAssertion(It.IsAny<BackendServicesJwtRequest>())).Returns("signed-jwt");

        var service = CreateService(handler, secretProvider, jwtFactory);
        var request = new BackendAuthScopesRequest(
            "https://fhir.epic.com/oauth2/token", "cid", null!, "kid-1", "vault", "secret-name", null, null, null);

        var result = await service.ProbeGrantedScopesAsync(request, CancellationToken.None);

        result.Success.Should().BeTrue();
        handler.Body.Should().Contain("client_assertion=signed-jwt");
    }

    [Fact]
    public async Task Requested_scope_falls_back_to_the_default_wildcard_read_scope_when_none_is_supplied()
    {
        var handler = new CapturingHandler();
        var service = CreateService(handler);
        var request = new BackendAuthScopesRequest(
            "https://athenahealth.example/oauth2/token", "cid", "secret", null, null, null, "s3cr3t", "post", null);

        await service.ProbeGrantedScopesAsync(request, CancellationToken.None);

        handler.Body.Should().Contain("scope=system%2F%2A.read");
    }

    // Epic's real 400 body, pretty-printed exactly as it arrives — the newlines are the whole point: they are what
    // made SafeErrorText reject the body and hand over to the leaky fallback.
    private const string EpicInvalidClientBody = """
        {
          "error": "invalid_client",
          "error_description": null
        }
        """;

    private const string EpicTokenEndpoint = "https://fhir.epic.com/interconnect-fhir-oauth/oauth2/token";

    /// <summary>
    /// The leak this path used to have. Epic pretty-prints its OAuth error body, so SafeErrorText's newline check
    /// rejected it — and the fallback that took over embedded that same body inside an internal format string
    /// which ALSO carried the client id, token endpoint and requested scope. The internal string was therefore the
    /// normal output for every Epic rejection, not a rare edge case.
    /// </summary>
    [Fact]
    public async Task A_rejection_does_not_leak_the_client_id_endpoint_scope_or_raw_body_to_the_client()
    {
        var handler = new CapturingHandler(HttpStatusCode.BadRequest, EpicInvalidClientBody);
        var service = CreateService(handler);
        var request = new BackendAuthScopesRequest(
            EpicTokenEndpoint, "0184f963-e044-4af2-a2fc-7546ec71ceac", "secret",
            null, null, null, "s3cr3t", "post", "system/*.rs");

        var result = await service.ProbeGrantedScopesAsync(request, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().NotBeNull();
        result.Error.Should().NotContain("0184f963", "the client id belongs in the log, not the browser");
        result.Error.Should().NotContain("system/*.rs");
        result.Error.Should().NotContain("interconnect-fhir-oauth");
        result.Error.Should().NotContain("error_description");
        result.Error.Should().NotContain("Backend-services token exchange failed");
    }

    [Fact]
    public async Task A_rejection_names_the_authorization_server_and_the_oauth_reason()
    {
        var handler = new CapturingHandler(HttpStatusCode.BadRequest, EpicInvalidClientBody);
        var service = CreateService(handler);
        var request = new BackendAuthScopesRequest(
            EpicTokenEndpoint, "cid", "secret", null, null, null, "s3cr3t", "post", "system/*.rs");

        var result = await service.ProbeGrantedScopesAsync(request, CancellationToken.None);

        result.Error.Should().Contain("fhir.epic.com");
        result.Error.Should().Contain("invalid_client");
        result.Error.Should().Contain("client ID");
    }

    /// <summary>
    /// Same fix as the token providers, applied to the wizard: an outage must not be worded as a credentials
    /// problem here either.
    /// </summary>
    [Fact]
    public async Task An_unavailable_authorization_server_is_worded_as_an_outage_not_as_bad_credentials()
    {
        var handler = new CapturingHandler(HttpStatusCode.ServiceUnavailable, "<html>Service Unavailable</html>");
        var service = CreateService(handler);
        var request = new BackendAuthScopesRequest(
            EpicTokenEndpoint, "cid", "secret", null, null, null, "s3cr3t", "post", "system/*.rs");

        var result = await service.ProbeGrantedScopesAsync(request, CancellationToken.None);

        result.Error.Should().Contain("not available");
        result.Error.Should().Contain("not a problem with this connection's credentials");
        result.Error.Should().NotContain("<html>", "an HTML error page must never reach the browser");
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _statusCode;
        private readonly string _responseBody;

        public string? Body { get; private set; }
        public System.Net.Http.Headers.AuthenticationHeaderValue? AuthorizationHeader { get; private set; }

        public CapturingHandler(
            HttpStatusCode statusCode = HttpStatusCode.OK,
            string responseBody = """{ "access_token": "minted-token", "expires_in": 300, "scope": "system/Patient.read" }""")
        {
            _statusCode = statusCode;
            _responseBody = responseBody;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            AuthorizationHeader = request.Headers.Authorization;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(_statusCode) { Content = new StringContent(_responseBody) };
        }
    }
}

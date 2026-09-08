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
    private static Mock<IHttpClientFactory> HttpClientFactory(HttpMessageHandler handler)
    {
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient(handler));
        return factory;
    }

    private static BackendAuthScopeProbeService CreateService(
        HttpMessageHandler handler,
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

    [Fact]
    public async Task A_vendor_with_no_scope_profile_keeps_the_requested_scope_verbatim()
    {
        var handler = new CapturingHandler();
        var service = CreateService(handler);
        var request = new BackendAuthScopesRequest(
            "https://fhir.epic.com/oauth2/token", "cid", "secret", null, null, null, "s3cr3t", "post",
            "system/*.rs", "Epic");

        await service.ProbeGrantedScopesAsync(request, CancellationToken.None);

        handler.Body.Should().Contain("scope=system%2F%2A.rs");
    }

    [Fact]
    public async Task ECW_wildcard_is_respelled_to_the_only_access_level_it_publishes()
    {
        // eCW rejects the generic 'system/*.rs' (and 'system/*.read') with invalid_scope and fails the whole token
        // request — the credential gate would report broken credentials for credentials that are fine.
        var handler = new CapturingHandler();
        var service = CreateService(handler);
        var request = new BackendAuthScopesRequest(
            "https://fhir4.healow.com/oauth2/token", "cid", "secret", null, null, null, "s3cr3t", "post",
            "system/*.rs", "Healow");

        await service.ProbeGrantedScopesAsync(request, CancellationToken.None);

        handler.Body.Should().Contain("scope=system%2F%2A.r");
    }

    [Fact]
    public async Task ECW_resource_scopes_take_each_resource_types_own_access_level()
    {
        var handler = new CapturingHandler();
        var service = CreateService(handler);
        var request = new BackendAuthScopesRequest(
            "https://fhir4.healow.com/oauth2/token", "cid", "secret", null, null, null, "s3cr3t", "post",
            "system/Patient.rs system/ServiceRequest.rs", "Healow");

        await service.ProbeGrantedScopesAsync(request, CancellationToken.None);

        // Form-encoded: '+' for the space between the two scopes.
        Uri.UnescapeDataString(handler.Body!.Replace('+', ' '))
            .Should().Contain("scope=system/Patient.read system/ServiceRequest.r");
    }

    [Fact]
    public async Task ECW_falls_back_to_its_wildcard_when_every_requested_resource_type_is_unsupported()
    {
        var handler = new CapturingHandler();
        var service = CreateService(handler);
        var request = new BackendAuthScopesRequest(
            "https://fhir4.healow.com/oauth2/token", "cid", "secret", null, null, null, "s3cr3t", "post",
            "system/Appointment.rs", "Healow");

        await service.ProbeGrantedScopesAsync(request, CancellationToken.None);

        handler.Body.Should().Contain("scope=system%2F%2A.r");
    }

    [Fact]
    public async Task A_wildcard_rejected_as_invalid_scope_is_retried_under_the_narrower_patient_scope()
    {
        // eCW registrations select explicit resource scopes, so even its correctly-spelled 'system/*.r' can come
        // back invalid_scope — the gate must still be able to tell the operator the credentials themselves work.
        var handler = new SequenceHandler(
            new HandlerStep(HttpStatusCode.BadRequest, """{"error":"invalid_scope"}"""),
            new HandlerStep(HttpStatusCode.OK, TokenBody));
        var service = CreateService(handler);
        var request = new BackendAuthScopesRequest(
            "https://fhir4.healow.com/oauth2/token", "cid", "secret", null, null, null, "s3cr3t", "post",
            "system/*.rs", "Healow");

        var result = await service.ProbeGrantedScopesAsync(request, CancellationToken.None);

        result.Success.Should().BeTrue();
        handler.Bodies.Should().HaveCount(2);
        handler.Bodies[0].Should().Contain("scope=system%2F%2A.r");
        handler.Bodies[1].Should().Contain("scope=system%2FPatient.read");
    }

    [Fact]
    public async Task A_rejection_that_is_not_about_scope_is_not_retried()
    {
        var handler = new SequenceHandler(
            new HandlerStep(HttpStatusCode.Unauthorized, """{"error":"invalid_client"}"""),
            new HandlerStep(HttpStatusCode.OK, TokenBody));
        var service = CreateService(handler);
        var request = new BackendAuthScopesRequest(
            "https://fhir4.healow.com/oauth2/token", "cid", "secret", null, null, null, "wrong", "post",
            "system/*.rs", "Healow");

        var result = await service.ProbeGrantedScopesAsync(request, CancellationToken.None);

        result.Success.Should().BeFalse();
        handler.Bodies.Should().HaveCount(1);
    }

    [Fact]
    public async Task A_rejected_per_resource_scope_string_also_narrows_to_the_patient_scope()
    {
        var handler = new SequenceHandler(
            new HandlerStep(HttpStatusCode.BadRequest, """{"error":"invalid_scope"}"""),
            new HandlerStep(HttpStatusCode.OK, TokenBody));
        var service = CreateService(handler);
        var request = new BackendAuthScopesRequest(
            "https://fhir4.healow.com/oauth2/token", "cid", "secret", null, null, null, "s3cr3t", "post",
            "system/Patient.rs system/Observation.rs", "Healow");

        var result = await service.ProbeGrantedScopesAsync(request, CancellationToken.None);

        result.Success.Should().BeTrue();
        handler.Bodies.Should().HaveCount(2);
        handler.Bodies[1].Should().Contain("scope=system%2FPatient.read");
    }

    [Fact]
    public async Task A_scope_reported_as_a_generic_invalid_request_400_still_narrows()
    {
        // eCW's real response to a scope its app registration doesn't include — no 'invalid_scope' anywhere in it.
        var handler = new SequenceHandler(
            new HandlerStep(
                HttpStatusCode.BadRequest,
                """{"error_description":"invalid_request","error":"400","error_uri":"http://www.hl7.org/fhir/smart-app-launch/"}"""),
            new HandlerStep(HttpStatusCode.OK, TokenBody));
        var service = CreateService(handler);
        var request = new BackendAuthScopesRequest(
            "https://fhir4.healow.com/oauth2/token", "cid", "secret", null, null, null, "s3cr3t", "post",
            "system/*.rs", "Healow");

        var result = await service.ProbeGrantedScopesAsync(request, CancellationToken.None);

        result.Success.Should().BeTrue();
        handler.Bodies.Should().HaveCount(2);
        handler.Bodies[1].Should().Contain("scope=system%2FPatient.read");
    }

    [Fact]
    public async Task A_400_that_names_a_credential_fault_is_not_retried()
    {
        var handler = new SequenceHandler(
            new HandlerStep(HttpStatusCode.BadRequest, """{"error":"invalid_client"}"""),
            new HandlerStep(HttpStatusCode.OK, TokenBody));
        var service = CreateService(handler);
        var request = new BackendAuthScopesRequest(
            "https://fhir4.healow.com/oauth2/token", "cid", "secret", null, null, null, "wrong", "post",
            "system/*.rs", "Healow");

        var result = await service.ProbeGrantedScopesAsync(request, CancellationToken.None);

        result.Success.Should().BeFalse();
        handler.Bodies.Should().HaveCount(1);
    }

    [Fact]
    public async Task Each_attempt_signs_its_own_assertion_so_a_single_use_jti_is_never_replayed()
    {
        // eCW rejects a replayed jti with a bare 400 invalid_request, which would mask the real reason the first
        // attempt failed — so the retry must carry a freshly signed assertion, not the first one again.
        var handler = new SequenceHandler(
            new HandlerStep(HttpStatusCode.BadRequest, """{"error":"invalid_scope"}"""),
            new HandlerStep(HttpStatusCode.OK, TokenBody));
        var secretProvider = new Mock<ISecretProvider>();
        secretProvider
            .Setup(s => s.GetSecretAsync(It.IsAny<SecretReference>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("pem");
        var jwtFactory = new Mock<IBackendServicesJwtFactory>();
        var signed = 0;
        jwtFactory
            .Setup(j => j.CreateClientAssertion(It.IsAny<BackendServicesJwtRequest>()))
            .Returns(() => $"signed-jwt-{++signed}");

        var service = CreateService(handler, secretProvider, jwtFactory);
        var request = new BackendAuthScopesRequest(
            "https://fhir4.healow.com/oauth2/token", "cid", "jwt", "kid-1", "vault", "secret-name", null, null,
            "system/*.rs", "Healow");

        var result = await service.ProbeGrantedScopesAsync(request, CancellationToken.None);

        result.Success.Should().BeTrue();
        signed.Should().Be(2);
        handler.Bodies.Should().HaveCount(2);
        handler.Bodies[0].Should().Contain("client_assertion=signed-jwt-1");
        handler.Bodies[1].Should().Contain("client_assertion=signed-jwt-2");
        // The signing key itself is resolved once, not per attempt.
        secretProvider.Verify(
            s => s.GetSecretAsync(It.IsAny<SecretReference>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task When_every_attempt_fails_the_first_failure_is_what_is_reported()
    {
        var handler = new SequenceHandler(
            new HandlerStep(HttpStatusCode.BadRequest, """{"error":"invalid_scope","error_description":"the-real-reason"}"""),
            new HandlerStep(HttpStatusCode.BadRequest, """{"error":"invalid_scope","error_description":"narrowed-attempt"}"""));
        var service = CreateService(handler);
        var request = new BackendAuthScopesRequest(
            "https://fhir4.healow.com/oauth2/token", "cid", "secret", null, null, null, "s3cr3t", "post",
            "system/*.rs", "Healow");

        var result = await service.ProbeGrantedScopesAsync(request, CancellationToken.None);

        result.Success.Should().BeFalse();
        handler.Bodies.Should().HaveCount(2);
        result.Error.Should().Contain("the-real-reason").And.NotContain("narrowed-attempt");
    }

    private const string TokenBody =
        """{ "access_token": "minted-token", "expires_in": 300, "scope": "system/Patient.read" }""";

    private sealed record HandlerStep(HttpStatusCode StatusCode, string ResponseBody);

    /// <summary>Replies with each step in turn (repeating the last), recording every request body — so a test can
    /// assert both what was retried and what was not.</summary>
    private sealed class SequenceHandler : HttpMessageHandler
    {
        private readonly HandlerStep[] _steps;
        private int _callCount;

        public List<string> Bodies { get; } = [];

        public SequenceHandler(params HandlerStep[] steps)
        {
            _steps = steps;
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Bodies.Add(request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken));
            var step = _steps[Math.Min(_callCount++, _steps.Length - 1)];
            return new HttpResponseMessage(step.StatusCode) { Content = new StringContent(step.ResponseBody) };
        }
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

using FHIRBridge.Application.Abstractions.Audit;
using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.Abstractions.Sources;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Domain.Aggregates;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Domain.Enums;
using FHIRBridge.Domain.ValueObjects;
using FHIRBridge.Infrastructure.Sources;
using FHIRBridge.Runtime.Application.Abstractions.Auth;
using FHIRBridge.Runtime.Application.DTOs;
using FHIRBridge.Runtime.Infrastructure.Auth;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace FHIRBridge.UnitTests.Sources;

public sealed class InteractiveSourceAuthorizationServiceTests
{
    private readonly Mock<ITenantConfigurationRepository> _tenantRepository = new();
    private readonly Mock<ISourceCapabilityDiscoveryService> _discovery = new();
    private readonly Mock<IInteractiveAuthorizationFlow> _flow = new();
    private readonly InMemoryOAuthAuthorizationStateStore _stateStore = new();
    private readonly Mock<ISecretProvider> _secretProvider = new();
    private readonly Mock<IOperationalAuditService> _audit = new();
    private readonly Mock<ICurrentUserService> _currentUser = new();

    private static readonly Guid TenantId = Guid.NewGuid();

    public InteractiveSourceAuthorizationServiceTests()
    {
        _currentUser.SetupGet(x => x.CurrentUser)
            .Returns(new CurrentUserInfo("admin", "admin@example.com", "Admin", ["Administrator"], true));
    }

    private InteractiveSourceAuthorizationService Service() => new(
        _tenantRepository.Object,
        _discovery.Object,
        _flow.Object,
        _stateStore,
        _secretProvider.Object,
        _audit.Object,
        _currentUser.Object,
        NullLogger<InteractiveSourceAuthorizationService>.Instance);

    private SourceConnection SeedEpicSource(
        string clientId = "client-1",
        SourceInteractiveConfiguration? interactive = null,
        SecretReference? clientSecret = null)
    {
        var tenant = new Tenant("Contoso Health", "contoso");
        var auth = new SourceAuthenticationConfiguration(
            AuthenticationType.OAuthClientCredentials, clientId, null, ["user/Patient.read"], clientSecret, null, null);
        var source = tenant.AddSourceConnection(
            "Epic Standalone", SourceSystemType.Epic, "https://fhir.example.com", auth, interactive: interactive);

        _tenantRepository.Setup(x => x.GetByIdAsync(TenantId, It.IsAny<CancellationToken>())).ReturnsAsync(tenant);
        return source;
    }

    private void SetupDiscovery(string? authorize = "https://auth.example.com/authorize", string? token = "https://auth.example.com/token")
    {
        _discovery.Setup(x => x.DiscoverSmartConfigurationAsync(TenantId, It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SmartConfigurationDto(authorize, token, null, null, null, [], [], [], [], []));
    }

    [Fact]
    public async Task StartAsync_returns_authorize_url_and_persists_the_pending_state()
    {
        var source = SeedEpicSource();
        SetupDiscovery();
        _flow.Setup(x => x.BuildAuthorizationRequest(
                It.IsAny<FhirSourceConfiguration>(), "https://app.example.com/api/v1/oauth/callback", It.IsAny<string>(), It.IsAny<string?>()))
            .Returns((FhirSourceConfiguration _, string redirect, string state, string? launch) =>
                new SmartAuthorizationRequest($"https://auth.example.com/authorize?state={state}", "verifier-1", state));

        var url = await Service().StartAsync(TenantId, source.Id, "https://app.example.com/api/v1/oauth/callback", CancellationToken.None);

        url.ToString().Should().StartWith("https://auth.example.com/authorize?state=");

        // The state carried in the URL must resolve to a saved, single-use pending authorization for this source.
        var state = System.Web.HttpUtility.ParseQueryString(url.Query)["state"]!;
        var pending = await _stateStore.TakeAsync(state, CancellationToken.None);
        pending.Should().NotBeNull();
        pending!.SourceConnectionId.Should().Be(source.Id);
        pending.CodeVerifier.Should().Be("verifier-1");
        pending.RedirectUri.Should().Be("https://app.example.com/api/v1/oauth/callback");
        pending.TokenEndpoint.Should().Be("https://auth.example.com/token");
    }

    [Fact]
    public async Task StartAsync_fails_when_the_source_advertises_no_authorization_endpoint()
    {
        var source = SeedEpicSource();
        SetupDiscovery(authorize: null);

        var act = () => Service().StartAsync(TenantId, source.Id, "https://app.example.com/api/v1/oauth/callback", CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task StartAsync_fails_when_the_source_has_no_client_id()
    {
        var source = SeedEpicSource(clientId: "");
        SetupDiscovery();

        var act = () => Service().StartAsync(TenantId, source.Id, "https://app.example.com/api/v1/oauth/callback", CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task CompleteAsync_exchanges_the_code_with_the_retained_verifier_and_redirect()
    {
        var source = SeedEpicSource();
        await _stateStore.SaveAsync("state-xyz", new PendingAuthorization(
            TenantId, source.Id, FHIRBridge.Runtime.Domain.Enums.RuntimeSourceType.Epic, "Epic Standalone",
            "verifier-1", "https://app.example.com/api/v1/oauth/callback", "https://auth.example.com/token", "client-1"),
            CancellationToken.None);
        var sourceConnectionId = source.Id;

        string? usedVerifier = null;
        string? usedRedirect = null;
        _flow.Setup(x => x.ExchangeAuthorizationCodeAsync(
                It.IsAny<FhirSourceConfiguration>(), "auth-code", It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback((FhirSourceConfiguration _, string _, string verifier, string redirect, CancellationToken _) =>
            {
                usedVerifier = verifier;
                usedRedirect = redirect;
            })
            .ReturnsAsync("access-token");

        var result = await Service().CompleteAsync("state-xyz", "auth-code", CancellationToken.None);

        result.SourceConnectionId.Should().Be(sourceConnectionId);
        usedVerifier.Should().Be("verifier-1");
        usedRedirect.Should().Be("https://app.example.com/api/v1/oauth/callback");

        // State is single-use — a replay finds nothing.
        (await _stateStore.TakeAsync("state-xyz", CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task CompleteAsync_fails_for_an_unknown_or_replayed_state()
    {
        var act = () => Service().CompleteAsync("never-issued", "auth-code", CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task CompleteAsync_resolves_and_forwards_the_confidential_client_secret()
    {
        var source = SeedEpicSource(clientSecret: new SecretReference("vault", "epic-client-secret"));
        _secretProvider.Setup(x => x.GetSecretAsync(
                It.Is<SecretReference>(s => s.SecretName == "epic-client-secret"), It.IsAny<CancellationToken>()))
            .ReturnsAsync("resolved-secret");
        await _stateStore.SaveAsync("state-conf", new PendingAuthorization(
            TenantId, source.Id, FHIRBridge.Runtime.Domain.Enums.RuntimeSourceType.Epic, "Epic Standalone",
            "verifier-1", "https://app/callback", "https://auth.example.com/token", "client-1"),
            CancellationToken.None);

        FhirSourceConfiguration? exchanged = null;
        _flow.Setup(x => x.ExchangeAuthorizationCodeAsync(
                It.IsAny<FhirSourceConfiguration>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback((FhirSourceConfiguration s, string _, string _, string _, CancellationToken _) => exchanged = s)
            .ReturnsAsync("access-token");

        await Service().CompleteAsync("state-conf", "auth-code", CancellationToken.None);

        // The resolved secret is carried into the exchange, where the provider turns it into client authentication.
        exchanged.Should().NotBeNull();
        exchanged!.ClientSecret.Should().Be("resolved-secret");
    }

    [Fact]
    public async Task StartAsync_adds_launch_patient_scope_for_the_launch_patient_selection_method()
    {
        var source = SeedEpicSource(interactive: new SourceInteractiveConfiguration(
            ["https://app.example.com/api/v1/oauth/callback"], null, [], PatientSelectionMethod.LaunchPatient));
        SetupDiscovery();
        FhirSourceConfiguration? captured = null;
        _flow.Setup(x => x.BuildAuthorizationRequest(It.IsAny<FhirSourceConfiguration>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>()))
            .Callback((FhirSourceConfiguration s, string _, string _, string? _) => captured = s)
            .Returns(new SmartAuthorizationRequest("https://auth.example.com/authorize", "verifier-1", "state"));

        await Service().StartAsync(TenantId, source.Id, "https://fallback/callback", CancellationToken.None);

        captured.Should().NotBeNull();
        captured!.Scopes.Should().Contain("launch/patient");
    }

    [Fact]
    public async Task StartEhrLaunchAsync_rejects_an_issuer_that_is_not_in_the_trusted_allow_list()
    {
        var source = SeedEpicSource(interactive: new SourceInteractiveConfiguration(
            ["https://app.example.com/api/v1/oauth/callback"], null, ["https://ehr.trusted.com/fhir"]));
        SetupDiscovery();

        var act = () => Service().StartEhrLaunchAsync(
            TenantId, source.Id, "https://evil.attacker.com/fhir", "launch-token", "https://fallback/callback", CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task StartEhrLaunchAsync_forwards_the_launch_token_for_a_trusted_issuer()
    {
        var source = SeedEpicSource(interactive: new SourceInteractiveConfiguration(
            ["https://app.example.com/api/v1/oauth/callback"], null, ["https://ehr.trusted.com/fhir"]));
        SetupDiscovery();
        string? forwardedLaunch = null;
        _flow.Setup(x => x.BuildAuthorizationRequest(It.IsAny<FhirSourceConfiguration>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>()))
            .Callback((FhirSourceConfiguration _, string _, string _, string? launch) => forwardedLaunch = launch)
            .Returns(new SmartAuthorizationRequest("https://auth.example.com/authorize", "verifier-1", "state"));

        await Service().StartEhrLaunchAsync(
            TenantId, source.Id, "https://ehr.trusted.com/fhir/", "launch-token", "https://fallback/callback", CancellationToken.None);

        forwardedLaunch.Should().Be("launch-token");
    }
}

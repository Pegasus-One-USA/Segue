using FHIRBridge.Application.Abstractions.Audit;
using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.Abstractions.Pipeline;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.Abstractions.Sources;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Domain.Enums;
using FHIRBridge.Domain.ValueObjects;
using FHIRBridge.Infrastructure.Security;
using FHIRBridge.Infrastructure.Sources;
using FHIRBridge.Runtime.Application.Abstractions.Auth;
using FHIRBridge.Runtime.Application.DTOs;
using FHIRBridge.Runtime.Infrastructure.Auth;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace FHIRBridge.UnitTests.Sources;

public sealed class InteractiveSourceAuthorizationServiceTests
{
    private readonly Mock<IConfigurationRepository> _configurationRepository = new();
    private readonly Mock<ISourceCapabilityDiscoveryService> _discovery = new();
    private readonly Mock<IInteractiveAuthorizationFlow> _flow = new();
    private readonly InMemoryOAuthAuthorizationStateStore _stateStore = new();
    private readonly Mock<ISecretProvider> _secretProvider = new();
    private readonly ILaunchTokenProtector _protector = new DataProtectionLaunchTokenProtector(new EphemeralDataProtectionProvider());
    private readonly Mock<IConfiguredPipelineService> _pipeline = new();
    private readonly Mock<IOperationalAuditService> _audit = new();
    private readonly Mock<ICurrentUserService> _currentUser = new();

    public InteractiveSourceAuthorizationServiceTests()
    {
        _currentUser.SetupGet(x => x.CurrentUser)
            .Returns(new CurrentUserInfo("admin", "admin@example.com", "Admin", ["Administrator"], true));
    }

    private InteractiveSourceAuthorizationService Service() => new(
        _configurationRepository.Object,
        _discovery.Object,
        _flow.Object,
        _stateStore,
        _secretProvider.Object,
        _protector,
        _pipeline.Object,
        _audit.Object,
        _currentUser.Object,
        NullLogger<InteractiveSourceAuthorizationService>.Instance);

    private SourceConnection SeedEpicSource(
        string clientId = "client-1",
        SourceInteractiveConfiguration? interactive = null,
        SecretReference? clientSecret = null)
    {
        var auth = new SourceAuthenticationConfiguration(
            AuthenticationType.OAuthClientCredentials, clientId, null, ["user/Patient.read"], clientSecret, null, null);
        var source = new SourceConnection(
            "Epic Standalone", SourceSystemType.Epic, "https://fhir.example.com", auth, interactive: interactive);

        _configurationRepository
            .Setup(x => x.GetSourceConnectionAsync(source.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(source);
        return source;
    }

    // Seeds the full chain a route-scoped launch resolves through: source + destination + mapping + route.
    private (Guid RouteId, SourceConnection Source) SeedRoute(SourceInteractiveConfiguration? interactive = null)
    {
        var auth = new SourceAuthenticationConfiguration(
            AuthenticationType.OAuthClientCredentials, "client-1", null, ["user/Patient.read"], null, null, null);
        var source = new SourceConnection(
            "Epic EHR Launch", SourceSystemType.Epic, "https://fhir.example.com", auth, interactive: interactive);
        var destination = new DestinationConfiguration(
            "SQL", DestinationType.SqlServer, new SecretReference("vault", "sql-conn"), "dbo.Observations");
        var mapping = new MappingProfile(
            "Observation → SQL", "Observation", source.Id, destination.Id, "dbo.Observations", Array.Empty<MappingField>());
        var route = new ResourcePipelineRoute(
            null, mapping.Id, IngestionMode.ScheduledPull, null, null, true, 1);

        _configurationRepository
            .Setup(x => x.GetSourceConnectionAsync(source.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(source);
        _configurationRepository
            .Setup(x => x.GetMappingProfileAsync(mapping.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(mapping);
        _configurationRepository
            .Setup(x => x.GetRouteAsync(route.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(route);
        // A launch fans out across every enabled route bound to the source, resolved from these collection getters.
        _configurationRepository
            .Setup(x => x.GetRoutesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { route });
        _configurationRepository
            .Setup(x => x.GetMappingProfilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { mapping });
        return (route.Id, source);
    }

    private void SetupDiscovery(string? authorize = "https://auth.example.com/authorize", string? token = "https://auth.example.com/token")
    {
        _discovery.Setup(x => x.DiscoverSmartConfigurationAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SmartConfigurationDto(authorize, token, null, null, null, [], [], [], [], [], []));
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

        var url = await Service().StartAsync(source.Id, "https://app.example.com/api/v1/oauth/callback", CancellationToken.None);

        url.ToString().Should().StartWith("https://auth.example.com/authorize?state=");

        // The state in the URL is an encrypted token; decrypting it yields the single-use nonce that keys the store.
        var state = System.Web.HttpUtility.ParseQueryString(url.Query)["state"]!;
        var nonce = _protector.UnprotectState(state);
        nonce.Should().NotBeNull();
        var pending = await _stateStore.TakeAsync(nonce!, CancellationToken.None);
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

        var act = () => Service().StartAsync(source.Id, "https://app.example.com/api/v1/oauth/callback", CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task StartAsync_fails_when_the_source_has_no_client_id()
    {
        var source = SeedEpicSource(clientId: "");
        SetupDiscovery();

        var act = () => Service().StartAsync(source.Id, "https://app.example.com/api/v1/oauth/callback", CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task CompleteAsync_exchanges_the_code_with_the_retained_verifier_and_redirect()
    {
        var source = SeedEpicSource();
        const string nonce = "nonce-xyz";
        await _stateStore.SaveAsync(nonce, new PendingAuthorization(
            source.Id, FHIRBridge.Runtime.Domain.Enums.RuntimeSourceType.Epic, "Epic Standalone",
            "verifier-1", "https://app.example.com/api/v1/oauth/callback", "https://auth.example.com/token", "client-1"),
            CancellationToken.None);
        var state = _protector.ProtectState(nonce);

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

        var result = await Service().CompleteAsync(state, "auth-code", CancellationToken.None);

        result.SourceConnectionId.Should().Be(source.Id);
        usedVerifier.Should().Be("verifier-1");
        usedRedirect.Should().Be("https://app.example.com/api/v1/oauth/callback");

        // State is single-use — a replay finds nothing.
        (await _stateStore.TakeAsync(nonce, CancellationToken.None)).Should().BeNull();
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
        const string nonce = "nonce-conf";
        await _stateStore.SaveAsync(nonce, new PendingAuthorization(
            source.Id, FHIRBridge.Runtime.Domain.Enums.RuntimeSourceType.Epic, "Epic Standalone",
            "verifier-1", "https://app/callback", "https://auth.example.com/token", "client-1"),
            CancellationToken.None);

        FhirSourceConfiguration? exchanged = null;
        _flow.Setup(x => x.ExchangeAuthorizationCodeAsync(
                It.IsAny<FhirSourceConfiguration>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback((FhirSourceConfiguration s, string _, string _, string _, CancellationToken _) => exchanged = s)
            .ReturnsAsync("access-token");

        await Service().CompleteAsync(_protector.ProtectState(nonce), "auth-code", CancellationToken.None);

        // The resolved secret is carried into the exchange, where the provider turns it into client authentication.
        exchanged.Should().NotBeNull();
        exchanged!.ClientSecret.Should().Be("resolved-secret");
    }

    [Fact]
    public async Task CompleteAsync_triggers_the_route_pipeline_for_a_route_scoped_launch()
    {
        var (routeId, source) = SeedRoute();
        const string nonce = "nonce-route";
        await _stateStore.SaveAsync(nonce, new PendingAuthorization(
            source.Id, FHIRBridge.Runtime.Domain.Enums.RuntimeSourceType.Epic, "Epic EHR Launch",
            "verifier-1", "https://app/cb", "https://auth.example.com/token", "client-1", routeId),
            CancellationToken.None);
        _flow.Setup(x => x.ExchangeAuthorizationCodeAsync(
                It.IsAny<FhirSourceConfiguration>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("access-token");

        StartConfiguredPipelineRunRequest? runRequest = null;
        _pipeline.Setup(x => x.StartAsync(It.IsAny<StartConfiguredPipelineRunRequest>(), It.IsAny<CancellationToken>()))
            .Callback((StartConfiguredPipelineRunRequest r, CancellationToken _) => runRequest = r)
            .Returns(Task.FromResult<ConfiguredPipelineRunDto>(null!));

        await Service().CompleteAsync(_protector.ProtectState(nonce), "auth-code", CancellationToken.None);

        runRequest.Should().NotBeNull();
        runRequest!.RouteIds.Should().Contain(routeId);
    }

    [Fact]
    public async Task CompleteAsync_fans_the_launch_out_across_every_enabled_route_for_the_source()
    {
        // One source with three resource-type routes (Patient, Encounter, Observation); a fourth route is disabled and
        // a fifth belongs to a different source. A launch on any one route must run exactly the three enabled routes
        // of the launched source — the launch establishes the patient context once for all configured resource types.
        var auth = new SourceAuthenticationConfiguration(
            AuthenticationType.OAuthClientCredentials, "client-1", null, ["user/Patient.read"], null, null, null);
        var source = new SourceConnection("Epic EHR Launch", SourceSystemType.Epic, "https://fhir.example.com", auth);
        var other = new SourceConnection("Other", SourceSystemType.Epic, "https://other.example.com", auth);

        MappingProfile Map(string rt, Guid sourceId) => new(rt, rt, sourceId, Guid.NewGuid(), rt, Array.Empty<MappingField>());
        var patient = Map("Patient", source.Id);
        var encounter = Map("Encounter", source.Id);
        var observation = Map("Observation", source.Id);
        var condition = Map("Condition", source.Id);
        var otherMap = Map("Patient", other.Id);

        ResourcePipelineRoute Route(Guid mappingId, bool enabled) =>
            new(null, mappingId, IngestionMode.ScheduledPull, null, null, enabled, 1);
        var patientRoute = Route(patient.Id, enabled: true);
        var encounterRoute = Route(encounter.Id, enabled: true);
        var observationRoute = Route(observation.Id, enabled: true);
        var conditionRoute = Route(condition.Id, enabled: false);   // disabled → excluded
        var otherRoute = Route(otherMap.Id, enabled: true);         // different source → excluded

        _configurationRepository.Setup(x => x.GetSourceConnectionAsync(source.Id, It.IsAny<CancellationToken>())).ReturnsAsync(source);
        _configurationRepository.Setup(x => x.GetMappingProfileAsync(patient.Id, It.IsAny<CancellationToken>())).ReturnsAsync(patient);
        _configurationRepository.Setup(x => x.GetRouteAsync(patientRoute.Id, It.IsAny<CancellationToken>())).ReturnsAsync(patientRoute);
        _configurationRepository.Setup(x => x.GetRoutesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { patientRoute, encounterRoute, observationRoute, conditionRoute, otherRoute });
        _configurationRepository.Setup(x => x.GetMappingProfilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { patient, encounter, observation, condition, otherMap });

        const string nonce = "nonce-fanout";
        await _stateStore.SaveAsync(nonce, new PendingAuthorization(
            source.Id, FHIRBridge.Runtime.Domain.Enums.RuntimeSourceType.Epic, "Epic EHR Launch",
            "verifier-1", "https://app/cb", "https://auth.example.com/token", "client-1", patientRoute.Id),
            CancellationToken.None);
        _flow.Setup(x => x.ExchangeAuthorizationCodeAsync(
                It.IsAny<FhirSourceConfiguration>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("access-token");

        StartConfiguredPipelineRunRequest? runRequest = null;
        _pipeline.Setup(x => x.StartAsync(It.IsAny<StartConfiguredPipelineRunRequest>(), It.IsAny<CancellationToken>()))
            .Callback((StartConfiguredPipelineRunRequest r, CancellationToken _) => runRequest = r)
            .Returns(Task.FromResult<ConfiguredPipelineRunDto>(null!));

        await Service().CompleteAsync(_protector.ProtectState(nonce), "auth-code", CancellationToken.None);

        runRequest.Should().NotBeNull();
        runRequest!.RouteIds.Should().BeEquivalentTo(new[] { patientRoute.Id, encounterRoute.Id, observationRoute.Id });
        runRequest.RouteIds.Should().NotContain(conditionRoute.Id).And.NotContain(otherRoute.Id);
    }

    [Fact]
    public async Task StartEhrLaunchFromContextAsync_rejects_a_tampered_context()
    {
        var act = () => Service().StartEhrLaunchFromContextAsync(
            "not-a-valid-token", "https://ehr.trusted.com/fhir", "launch", "https://app/cb", CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task StartEhrLaunchFromContextAsync_resolves_the_route_and_forwards_the_launch_token()
    {
        var (routeId, _) = SeedRoute(new SourceInteractiveConfiguration(
            ["https://app.example.com/api/v1/oauth/callback"], null, ["https://ehr.trusted.com/fhir"]));
        SetupDiscovery();
        var context = _protector.ProtectContext(routeId);
        string? forwardedLaunch = null;
        _flow.Setup(x => x.BuildAuthorizationRequest(It.IsAny<FhirSourceConfiguration>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>()))
            .Callback((FhirSourceConfiguration _, string _, string _, string? launch) => forwardedLaunch = launch)
            .Returns(new SmartAuthorizationRequest("https://auth.example.com/authorize", "verifier-1", "state"));

        await Service().StartEhrLaunchFromContextAsync(
            context, "https://ehr.trusted.com/fhir", "launch-token", "https://fallback/cb", CancellationToken.None);

        forwardedLaunch.Should().Be("launch-token");
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

        await Service().StartAsync(source.Id, "https://fallback/callback", CancellationToken.None);

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
            source.Id, "https://evil.attacker.com/fhir", "launch-token", "https://fallback/callback", CancellationToken.None);

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
            source.Id, "https://ehr.trusted.com/fhir/", "launch-token", "https://fallback/callback", CancellationToken.None);

        forwardedLaunch.Should().Be("launch-token");
    }
}

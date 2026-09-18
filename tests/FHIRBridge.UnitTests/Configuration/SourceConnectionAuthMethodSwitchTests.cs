using FHIRBridge.Application.Abstractions.Mapping;
using FHIRBridge.Application.Abstractions.Sources;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Application.Services;
using FHIRBridge.Application.Validation;
using FHIRBridge.Domain.Enums;
using FHIRBridge.Infrastructure.Persistence;
using FHIRBridge.SharedKernel.Enums;
using FluentAssertions;
using Moq;

namespace FHIRBridge.UnitTests.Configuration;

/// <summary>
/// Regression coverage for the stale-signing-key-reference bug: a connection saved with SmartBackendServices (and
/// therefore a PrivateKey reference) that's later switched to a different AuthenticationType must not carry that
/// PrivateKey reference forward. PreserveSecretsIfBlank's general "blank field on update preserves it" rule exists
/// so re-saving a connection without re-entering a client secret / private key doesn't erase it — but for
/// PrivateKey specifically, that same preservation left a signing-key reference wired into a connection whose auth
/// method no longer used it, which SourceConnectionRuntimeResolver used to resolve unconditionally, failing runs
/// over a secret the run never needed and the vault might not even hold.
/// </summary>
public sealed class SourceConnectionAuthMethodSwitchTests
{
    private readonly InMemoryConfigurationRepository _repository = new(TestHelpers.LicenseTestScopeFactory.Create());
    private readonly ConfigurationService _sut;

    public SourceConnectionAuthMethodSwitchTests()
    {
        _sut = new ConfigurationService(
            _repository,
            new InMemorySourceCapabilityRepository(),
            Mock.Of<ISourceCapabilityDiscoveryService>(),
            Mock.Of<FHIRBridge.Application.Abstractions.Security.ISecretWriter>(),
            Mock.Of<FHIRBridge.Application.Abstractions.Security.ISecretProvider>(),
            Mock.Of<FHIRBridge.Application.Abstractions.Destinations.ISqlConnectionSecretMerger>(),
            new FHIRBridge.UnitTests.Security.PassthroughTenantSecretVaultResolver(),
            Mock.Of<IParentReferenceResolver>(),
            new CreateMappingProfileRequestValidator(
                new FHIRBridge.UnitTests.Validation.NoOpDestinationSchemaService(),
                _repository,
                new FHIRBridge.UnitTests.Validation.NoOpEffectiveRuleResolver()),
            new CreateDestinationConfigurationRequestValidator(),
            new FHIRBridge.UnitTests.Security.PassthroughUserDisplayNameResolver(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ConfigurationService>.Instance);
    }

    [Fact]
    public async Task Switching_away_from_SmartBackendServices_clears_the_leftover_private_key_reference()
    {
        var smartBackendServicesAuth = new SourceAuthenticationDto(
            AuthenticationType.SmartBackendServices,
            ClientId: "client-1",
            TokenEndpoint: "https://auth.example.com/token",
            Scopes: ["system/Patient.read"],
            ClientSecretKeyVaultName: null,
            ClientSecretName: null,
            PrivateKeyKeyVaultName: "signing-keys",
            PrivateKeySecretName: "athenahealth-private-key-abc123",
            KeyId: "key-1");

        var created = await _sut.AddSourceConnectionAsync(
            new CreateSourceConnectionRequest(
                $"Connection {Guid.NewGuid():N}", SourceSystemType.Athenahealth,
                "https://api.athenahealth.com/fhir/r4", smartBackendServicesAuth, ApplicationType.Backend),
            CancellationToken.None);

        created.Authentication.PrivateKeyKeyVaultName.Should().NotBeNull();
        created.Authentication.PrivateKeySecretName.Should().NotBeNull();

        var switchedToClientCredentials = smartBackendServicesAuth with
        {
            AuthenticationType = AuthenticationType.OAuthClientCredentials,
            PrivateKeyKeyVaultName = null,
            PrivateKeySecretName = null,
            KeyId = null,
        };

        var updated = await _sut.UpdateSourceConnectionAsync(
            created.Id,
            new CreateSourceConnectionRequest(
                created.Name, SourceSystemType.Athenahealth, created.BaseUrl,
                switchedToClientCredentials, ApplicationType.Backend),
            CancellationToken.None);

        updated.Authentication.AuthenticationType.Should().Be(AuthenticationType.OAuthClientCredentials);
        updated.Authentication.PrivateKeyKeyVaultName.Should().BeNull();
        updated.Authentication.PrivateKeySecretName.Should().BeNull();
    }

    [Fact]
    public async Task Resaving_SmartBackendServices_with_a_blank_private_key_field_still_preserves_it()
    {
        // Athenahealth, not Epic: ValidateEpicSourceConnection's "KeyId/private-key-reference required on every
        // request" rule is Epic-specific, so this exercises the general "blank field on update preserves it"
        // behavior PreserveSecretsIfBlank documents, without an unrelated vendor validation getting in the way.
        var smartBackendServicesAuth = new SourceAuthenticationDto(
            AuthenticationType.SmartBackendServices,
            ClientId: "client-1",
            TokenEndpoint: "https://auth.example.com/token",
            Scopes: ["system/Patient.read"],
            ClientSecretKeyVaultName: null,
            ClientSecretName: null,
            PrivateKeyKeyVaultName: "signing-keys",
            PrivateKeySecretName: "athenahealth-private-key-abc123",
            KeyId: "key-1");

        var created = await _sut.AddSourceConnectionAsync(
            new CreateSourceConnectionRequest(
                $"Connection {Guid.NewGuid():N}", SourceSystemType.Athenahealth,
                "https://api.athenahealth.com/fhir/r4", smartBackendServicesAuth, ApplicationType.Backend),
            CancellationToken.None);

        // Same shape a re-save from an edit form takes: still SmartBackendServices, but the private-key fields
        // come back blank because the form never re-displays a previously stored secret reference.
        var resavedWithBlankKeyFields = smartBackendServicesAuth with
        {
            PrivateKeyKeyVaultName = null,
            PrivateKeySecretName = null,
            KeyId = null,
        };

        var updated = await _sut.UpdateSourceConnectionAsync(
            created.Id,
            new CreateSourceConnectionRequest(
                created.Name, SourceSystemType.Athenahealth, created.BaseUrl,
                resavedWithBlankKeyFields, ApplicationType.Backend),
            CancellationToken.None);

        updated.Authentication.PrivateKeyKeyVaultName.Should().Be("signing-keys");
        updated.Authentication.PrivateKeySecretName.Should().Be("athenahealth-private-key-abc123");
    }

    [Fact]
    public async Task A_no_op_save_on_an_interactive_connection_that_never_used_SmartBackendServices_does_not_wipe_its_private_key()
    {
        // AuthenticationType.None is the real, legitimate value an interactive (non-Backend) connection's
        // confidential asymmetric client resolves to — SMART App Launch permits private_key_jwt outside Backend
        // Services too. Gating the clear on "existing WAS SmartBackendServices, requested is not" (rather than on
        // the requested type alone) must never touch a connection like this one, which was never
        // SmartBackendServices in the first place, even though its resolved AuthenticationType is None both
        // before and after this save.
        var confidentialInteractiveAuth = new SourceAuthenticationDto(
            AuthenticationType.None,
            ClientId: "client-1",
            TokenEndpoint: "https://auth.example.com/token",
            Scopes: ["launch/patient", "patient/Patient.read"],
            ClientSecretKeyVaultName: null,
            ClientSecretName: null,
            PrivateKeyKeyVaultName: "signing-keys",
            PrivateKeySecretName: "athenahealth-private-key-confidential-abc",
            KeyId: "key-1",
            AuthorizationEndpoint: "https://auth.example.com/authorize");
        var interactive = new SourceInteractiveConfigurationDto(
            RedirectUris: ["https://portal.example.com/callback"], LaunchUrl: null, TrustedIssuers: []);

        var created = await _sut.AddSourceConnectionAsync(
            new CreateSourceConnectionRequest(
                $"Connection {Guid.NewGuid():N}", SourceSystemType.Athenahealth,
                "https://api.athenahealth.com/fhir/r4", confidentialInteractiveAuth, ApplicationType.Patient,
                interactive),
            CancellationToken.None);

        // Same shape a no-op re-save takes: still resolves to None, private-key fields come back blank because
        // the form never re-displays a previously stored secret reference.
        var resavedWithBlankKeyFields = confidentialInteractiveAuth with
        {
            PrivateKeyKeyVaultName = null,
            PrivateKeySecretName = null,
            KeyId = null,
        };

        var updated = await _sut.UpdateSourceConnectionAsync(
            created.Id,
            new CreateSourceConnectionRequest(
                created.Name, SourceSystemType.Athenahealth, created.BaseUrl,
                resavedWithBlankKeyFields, ApplicationType.Patient, interactive),
            CancellationToken.None);

        updated.Authentication.AuthenticationType.Should().Be(AuthenticationType.None);
        updated.Authentication.PrivateKeyKeyVaultName.Should().Be("signing-keys");
        updated.Authentication.PrivateKeySecretName.Should().Be("athenahealth-private-key-confidential-abc");
    }
}

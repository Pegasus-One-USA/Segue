using FHIRBridge.Application.Abstractions.Mapping;
using FHIRBridge.Application.Abstractions.Security;
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
/// The mapping-save capability gate (<c>EnsureSourceSupportsResourceTypeAsync</c>) runs Epic capability discovery on
/// demand — but discovery authenticates as SMART Backend Services (private_key_jwt), which an interactive source
/// (EHR launch / standalone / patient) does not have at configuration time. These tests pin the branch: interactive
/// Epic sources skip discovery (fail open so authoring is not blocked), Backend Epic sources still discover.
/// </summary>
public sealed class MappingProfileCapabilityGatingTests
{
    private readonly InMemoryConfigurationRepository _repository = new(TestHelpers.LicenseTestScopeFactory.Create());
    private readonly InMemorySourceCapabilityRepository _capabilityRepository = new();
    private readonly Mock<ISourceCapabilityDiscoveryService> _discovery = new();
    private readonly ConfigurationService _sut;

    public MappingProfileCapabilityGatingTests()
    {
        _sut = new ConfigurationService(
            _repository, _capabilityRepository, _discovery.Object,
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
    public async Task Interactive_epic_source_skips_capability_discovery_on_mapping_save()
    {
        var source = await AddEpicSourceAsync(ApplicationType.EhrLaunch);

        var mapping = await _sut.AddMappingProfileAsync(
            NewMappingRequest(source.Id, "Patient"), CancellationToken.None);

        mapping.Should().NotBeNull();
        _discovery.Verify(
            x => x.DiscoverAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Backend_epic_source_runs_capability_discovery_on_mapping_save()
    {
        var source = await AddEpicSourceAsync(ApplicationType.Backend);

        await _sut.AddMappingProfileAsync(NewMappingRequest(source.Id, "Patient"), CancellationToken.None);

        _discovery.Verify(
            x => x.DiscoverAsync(source.Id, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>
    /// A brand-new Backend Services source whose signing key Epic hasn't been told about yet fails live discovery
    /// with an auth error (the same "invalid_client" the token endpoint returns) — this must fail OPEN (mapping
    /// still saves, resource type left unverified) rather than propagate and roll back the whole save, since the
    /// mapping save and the source-connection save it depends on are committed together in one transaction
    /// (WorkflowEndpoints' build handler) and a hard failure here would undo both, leaving no saved connection id
    /// to register a JWKS URL against in the first place.
    /// </summary>
    [Fact]
    public async Task Failed_capability_discovery_fails_open_instead_of_blocking_the_mapping_save()
    {
        var source = await AddEpicSourceAsync(ApplicationType.Backend);
        _discovery
            .Setup(x => x.DiscoverAsync(source.Id, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException(
                "Epic token endpoint returned 400 (Bad Request). Response body: { \"error\": \"invalid_client\" }"));

        var act = () => _sut.AddMappingProfileAsync(NewMappingRequest(source.Id, "Patient"), CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    private async Task<SourceConnectionDto> AddEpicSourceAsync(ApplicationType applicationType)
    {
        var isInteractive = applicationType != ApplicationType.Backend;
        var authentication = new SourceAuthenticationDto(
            isInteractive ? AuthenticationType.None : AuthenticationType.SmartBackendServices,
            ClientId: "client-1",
            TokenEndpoint: isInteractive ? null : "https://auth.epic.com/token",
            Scopes: ["user/Patient.read"],
            ClientSecretKeyVaultName: null,
            ClientSecretName: null,
            PrivateKeyKeyVaultName: isInteractive ? null : "kv",
            PrivateKeySecretName: isInteractive ? null : "epic-private-key",
            KeyId: isInteractive ? null : "key-1");

        var interactive = isInteractive
            ? new SourceInteractiveConfigurationDto(
                RedirectUris: ["https://app/callback"],
                LaunchUrl: null,
                TrustedIssuers: ["https://fhir.epic.com/interconnect-fhir-oauth/api/FHIR/R4"])
            : null;

        return await _sut.AddSourceConnectionAsync(
            new CreateSourceConnectionRequest(
                "Epic", SourceSystemType.Epic, "https://fhir.epic.com/api/FHIR/R4",
                authentication, applicationType, interactive),
            CancellationToken.None);
    }

    private static CreateMappingProfileRequest NewMappingRequest(Guid sourceId, string resourceType) =>
        new(
            $"{resourceType} → SQL",
            resourceType,
            sourceId,
            Guid.NewGuid(),
            resourceType,
            [new MappingFieldDto("id", "$.id", MappingValueType.String, IsRequired: true, DefaultValue: null, Format: null)]);
}

using FHIRBridge.Application.Abstractions.Audit;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.Abstractions.Sources;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Application.Services;
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
    private readonly InMemoryConfigurationRepository _repository = new();
    private readonly InMemorySourceCapabilityRepository _capabilityRepository = new();
    private readonly Mock<ISourceCapabilityDiscoveryService> _discovery = new();
    private readonly Mock<IOperationalAuditService> _audit = new();
    private readonly Mock<ICurrentUserService> _currentUser = new();
    private readonly ConfigurationService _sut;

    public MappingProfileCapabilityGatingTests()
    {
        _currentUser.SetupGet(x => x.CurrentUser)
            .Returns(new CurrentUserInfo("admin", "admin@example.com", "Admin", ["Administrator"], true));
        _sut = new ConfigurationService(
            _repository, _capabilityRepository, _discovery.Object, _audit.Object, _currentUser.Object,
            Mock.Of<FHIRBridge.Application.Abstractions.Security.ISecretWriter>());
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

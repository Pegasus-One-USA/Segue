using FHIRBridge.Application.Abstractions.Mapping;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.Abstractions.Sources;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Application.Services;
using FHIRBridge.Application.Validation;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Domain.Enums;
using FHIRBridge.Domain.ValueObjects;
using FHIRBridge.Infrastructure.Persistence;
using FHIRBridge.SharedKernel.Enums;
using FluentAssertions;
using Moq;

namespace FHIRBridge.UnitTests.Configuration;

/// <summary>
/// Covers <see cref="ConfigurationService.FindMappingProfileAsync"/> — the lookup <c>POST /workflows/build</c>'s
/// mapping loop (<c>WorkflowEndpoints.cs</c>) now uses to detect an already-existing MappingProfile for a
/// (resourceType, sourceConnectionId, destinationId) combination before deciding whether to reuse it as-is
/// (when authored by the richer Mapping Config Import wizard — signaled by a non-null <c>MappingJson</c>),
/// update it in place (this endpoint's own simpler path, no <c>MappingJson</c>), or create a new one.
/// </summary>
public sealed class ConfigurationServiceMappingProfileLookupTests
{
    private readonly InMemoryConfigurationRepository _repository = new(TestHelpers.LicenseTestScopeFactory.Create());
    private readonly ConfigurationService _sut;

    public ConfigurationServiceMappingProfileLookupTests()
    {
        _sut = new ConfigurationService(
            _repository,
            new InMemorySourceCapabilityRepository(),
            Mock.Of<ISourceCapabilityDiscoveryService>(),
            Mock.Of<ISecretWriter>(),
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
    public async Task Returns_null_when_no_profile_exists_for_the_combination()
    {
        var found = await _sut.FindMappingProfileAsync("Patient", Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None);

        found.Should().BeNull();
    }

    [Fact]
    public async Task Finds_a_profile_created_via_AddMappingProfileAsync_with_no_MappingJson()
    {
        var (sourceId, destinationId) = await AddSourceAndDestinationAsync();
        var created = await _sut.AddMappingProfileAsync(NewMappingRequest(sourceId, destinationId, "Patient"), CancellationToken.None);

        var found = await _sut.FindMappingProfileAsync("Patient", sourceId, destinationId, CancellationToken.None);

        found.Should().NotBeNull();
        found!.Id.Should().Be(created.Id);
        // Confirms the signal WorkflowEndpoints.cs relies on: this endpoint's own create path never marks a
        // profile as import-authored, so it stays eligible for in-place update on a later build.
        found.MappingJson.Should().BeNullOrEmpty();
    }

    [Fact]
    public async Task Finds_an_import_authored_profile_and_surfaces_its_MappingJson()
    {
        var (sourceId, destinationId) = await AddSourceAndDestinationAsync();
        var importedProfile = new MappingProfile(
            "Patient", "Patient", sourceId, destinationId, "Patient",
            [new MappingField("Id", "$.id", MappingValueType.String, IsRequired: false, DefaultValue: null, Format: "directField")],
            mappingJson: """{"mappings":[{"resourceType":"Patient"}]}""");
        await _repository.AddMappingProfileAsync(importedProfile, CancellationToken.None);

        var found = await _sut.FindMappingProfileAsync("Patient", sourceId, destinationId, CancellationToken.None);

        found.Should().NotBeNull();
        found!.Id.Should().Be(importedProfile.Id);
        found.MappingJson.Should().NotBeNullOrEmpty();
    }

    private async Task<(Guid SourceId, Guid DestinationId)> AddSourceAndDestinationAsync()
    {
        var source = await _sut.AddSourceConnectionAsync(
            new CreateSourceConnectionRequest(
                "Epic", SourceSystemType.Epic, "https://fhir.epic.com/api/FHIR/R4",
                new SourceAuthenticationDto(
                    AuthenticationType.None, ClientId: "client-1", TokenEndpoint: null, Scopes: ["user/Patient.read"],
                    ClientSecretKeyVaultName: null, ClientSecretName: null, PrivateKeyKeyVaultName: null,
                    PrivateKeySecretName: null, KeyId: null),
                ApplicationType.Standalone,
                new SourceInteractiveConfigurationDto(RedirectUris: ["https://app/callback"], LaunchUrl: null, TrustedIssuers: [])),
            CancellationToken.None);

        var destination = await _sut.AddDestinationConfigurationAsync(
            new CreateDestinationConfigurationRequest(
                "SQL Destination", DestinationType.SqlServer, "kv", "secret", "dbo.Patient"),
            CancellationToken.None);

        return (source.Id, destination.Id);
    }

    private static CreateMappingProfileRequest NewMappingRequest(Guid sourceId, Guid destinationId, string resourceType) =>
        new(
            $"{resourceType} → SQL",
            resourceType,
            sourceId,
            destinationId,
            resourceType,
            [new MappingFieldDto("id", "$.id", MappingValueType.String, IsRequired: true, DefaultValue: null, Format: null)]);
}

using FHIRBridge.Application.Abstractions.Mapping;
using FHIRBridge.Application.Abstractions.Sources;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Application.Services;
using FHIRBridge.Application.Validation;
using FHIRBridge.Domain.Enums;
using FHIRBridge.Infrastructure.Persistence;
using FHIRBridge.SharedKernel.Enums;
using FHIRBridge.SharedKernel.Exceptions;
using FluentAssertions;
using Moq;

namespace FHIRBridge.UnitTests.Configuration;

/// <summary>
/// Slice 2b (docs/backend/13-source-connection-configuration-split-plan.md): a mapping profile created without an
/// explicit SourceConfigurationId auto-provisions one from its connection's current settings (mirroring the Slice 1
/// migration backfill), and reuses that same configuration across saves instead of creating a new one every update —
/// the fix for the wizard's "editing search criteria forks a new connection" duplication bug, applied at the
/// Configured Pipeline layer.
/// </summary>
public sealed class SourceConfigurationAutoProvisionTests
{
    private readonly InMemoryConfigurationRepository _repository = new();
    private readonly ConfigurationService _sut;

    public SourceConfigurationAutoProvisionTests()
    {
        _sut = new ConfigurationService(
            _repository,
            new InMemorySourceCapabilityRepository(),
            Mock.Of<ISourceCapabilityDiscoveryService>(),
            Mock.Of<FHIRBridge.Application.Abstractions.Security.ISecretWriter>(),
            Mock.Of<IParentReferenceResolver>(),
            new CreateMappingProfileRequestValidator(new FHIRBridge.UnitTests.Validation.NoOpDestinationSchemaService()),
            new CreateDestinationConfigurationRequestValidator(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ConfigurationService>.Instance);
    }

    [Fact]
    public async Task Create_without_explicit_configuration_auto_provisions_one_from_the_connection()
    {
        var connection = await AddConnectionAsync(["system/Patient.rs"]);

        var mapping = await _sut.AddMappingProfileAsync(NewMappingRequest(connection.Id, "Patient"), CancellationToken.None);

        mapping.SourceConfigurationId.Should().NotBeNull();
        var configuration = await _sut.GetSourceConfigurationByIdAsync(mapping.SourceConfigurationId!.Value, CancellationToken.None);
        configuration.Should().NotBeNull();
        configuration!.ConnectionId.Should().Be(connection.Id);
        configuration.Scopes.Should().BeEquivalentTo(["system/Patient.rs"]);
    }

    [Fact]
    public async Task Update_without_changing_connection_reuses_the_existing_configuration()
    {
        var connection = await AddConnectionAsync(["system/Patient.rs"]);
        var mapping = await _sut.AddMappingProfileAsync(NewMappingRequest(connection.Id, "Patient"), CancellationToken.None);
        var originalConfigurationId = mapping.SourceConfigurationId;

        var updated = await _sut.UpdateMappingProfileAsync(
            mapping.Id, NewMappingRequest(connection.Id, "Patient", name: "Renamed"), CancellationToken.None);

        (updated.SourceConfigurationId == originalConfigurationId).Should().BeTrue();
        (await _repository.GetSourceConfigurationsAsync(CancellationToken.None)).Should().HaveCount(1);
    }

    [Fact]
    public async Task Update_pointing_at_a_different_connection_provisions_a_fresh_configuration()
    {
        var connectionA = await AddConnectionAsync(["system/Patient.rs"]);
        var connectionB = await AddConnectionAsync(["system/Observation.rs"]);
        var mapping = await _sut.AddMappingProfileAsync(NewMappingRequest(connectionA.Id, "Patient"), CancellationToken.None);

        var updated = await _sut.UpdateMappingProfileAsync(
            mapping.Id, NewMappingRequest(connectionB.Id, "Patient"), CancellationToken.None);

        (updated.SourceConfigurationId == mapping.SourceConfigurationId).Should().BeFalse();
        var configuration = await _sut.GetSourceConfigurationByIdAsync(updated.SourceConfigurationId!.Value, CancellationToken.None);
        configuration!.ConnectionId.Should().Be(connectionB.Id);
    }

    [Fact]
    public async Task Explicit_configuration_from_a_different_connection_is_rejected()
    {
        var connectionA = await AddConnectionAsync(["system/Patient.rs"]);
        var connectionB = await AddConnectionAsync(["system/Observation.rs"]);
        var foreignConfiguration = await _sut.AddSourceConfigurationAsync(
            new CreateSourceConfigurationRequest(connectionB.Id, "Foreign", ["system/Observation.rs"]),
            CancellationToken.None);

        var act = () => _sut.AddMappingProfileAsync(
            NewMappingRequest(connectionA.Id, "Patient") with { SourceConfigurationId = foreignConfiguration.Id },
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Explicit_valid_configuration_is_reused_without_provisioning_a_new_one()
    {
        var connection = await AddConnectionAsync(["system/Patient.rs"]);
        var existingConfiguration = await _sut.AddSourceConfigurationAsync(
            new CreateSourceConfigurationRequest(connection.Id, "Shared config", ["system/Patient.rs"]),
            CancellationToken.None);

        var mapping = await _sut.AddMappingProfileAsync(
            NewMappingRequest(connection.Id, "Patient") with { SourceConfigurationId = existingConfiguration.Id },
            CancellationToken.None);

        mapping.SourceConfigurationId.Should().Be(existingConfiguration.Id);
        (await _repository.GetSourceConfigurationsAsync(CancellationToken.None)).Should().HaveCount(1);
    }

    private async Task<SourceConnectionDto> AddConnectionAsync(string[] scopes)
    {
        var authentication = new SourceAuthenticationDto(
            AuthenticationType.SmartBackendServices,
            ClientId: "client-1",
            TokenEndpoint: "https://auth.example.com/token",
            Scopes: scopes,
            ClientSecretKeyVaultName: null,
            ClientSecretName: null,
            PrivateKeyKeyVaultName: "kv",
            PrivateKeySecretName: "private-key",
            KeyId: "key-1");

        return await _sut.AddSourceConnectionAsync(
            new CreateSourceConnectionRequest(
                $"Connection {Guid.NewGuid():N}", SourceSystemType.GenericFhir, "https://fhir.example.com/api/FHIR/R4",
                authentication, ApplicationType.Backend),
            CancellationToken.None);
    }

    private static CreateMappingProfileRequest NewMappingRequest(Guid sourceConnectionId, string resourceType, string? name = null) =>
        new(
            name ?? $"{resourceType} -> SQL",
            resourceType,
            sourceConnectionId,
            Guid.NewGuid(),
            resourceType,
            [new MappingFieldDto("id", "$.id", MappingValueType.String, IsRequired: true, DefaultValue: null, Format: null)]);
}

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
/// A composite route carries a primary mapping profile plus additional resource mappings, so one route can pull and
/// write several FHIR resource types (Patient, Encounter, Observation, …) in a single run. These tests pin the
/// service-side normalization: the primary is always included, children must share the primary's source, and
/// duplicates are rejected.
/// </summary>
public sealed class CompositeRouteTests
{
    private readonly InMemoryConfigurationRepository _repository = new();
    private readonly InMemorySourceCapabilityRepository _capabilityRepository = new();
    private readonly Mock<ISourceCapabilityDiscoveryService> _discovery = new();
    private readonly ConfigurationService _sut;

    public CompositeRouteTests()
    {
        _sut = new ConfigurationService(
            _repository, _capabilityRepository, _discovery.Object,
            Mock.Of<FHIRBridge.Application.Abstractions.Security.ISecretWriter>(),
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
    public async Task Composite_route_includes_the_primary_and_all_child_mappings()
    {
        var source = await AddInteractiveEpicSourceAsync();
        var patient = await AddMappingAsync(source.Id, "Patient");
        var encounter = await AddMappingAsync(source.Id, "Encounter");
        var observation = await AddMappingAsync(source.Id, "Observation");

        var route = await _sut.AddResourceRouteAsync("Patient", new CreateResourceRouteRequest(
            IngestionMode.ScheduledPull, null, patient.Id, "0 0 * * *", null, IsEnabled: true, Priority: 0,
            ResourceMappings:
            [
                new ResourceRouteMappingRequest(encounter.Id, IsEnabled: true, ExecutionOrder: 1),
                new ResourceRouteMappingRequest(observation.Id, IsEnabled: true, ExecutionOrder: 2)
            ]), CancellationToken.None);

        route.ResourceMappings.Select(m => m.MappingProfileId)
            .Should().BeEquivalentTo([patient.Id, encounter.Id, observation.Id]);
    }

    [Fact]
    public async Task Composite_route_persists_per_mapping_search_parameters()
    {
        var source = await AddInteractiveEpicSourceAsync();
        var patient = await AddMappingAsync(source.Id, "Patient");
        var observation = await AddMappingAsync(source.Id, "Observation");

        var route = await _sut.AddResourceRouteAsync("Patient", new CreateResourceRouteRequest(
            IngestionMode.ScheduledPull, null, patient.Id, "0 0 * * *", null, IsEnabled: true, Priority: 0,
            ResourceMappings:
            [
                new ResourceRouteMappingRequest(observation.Id, IsEnabled: true, ExecutionOrder: 1,
                    SearchParameters: "category=laboratory")
            ]), CancellationToken.None);

        route.ResourceMappings.Single(m => m.MappingProfileId == observation.Id)
            .SearchParameters.Should().Be("category=laboratory");
        route.ResourceMappings.Single(m => m.MappingProfileId == patient.Id)
            .SearchParameters.Should().BeNull();
    }

    [Fact]
    public async Task Composite_route_rejects_a_child_mapping_from_a_different_source()
    {
        var source = await AddInteractiveEpicSourceAsync();
        var otherSource = await AddInteractiveEpicSourceAsync("Other Epic");
        var patient = await AddMappingAsync(source.Id, "Patient");
        var foreignEncounter = await AddMappingAsync(otherSource.Id, "Encounter");

        var act = () => _sut.AddResourceRouteAsync("Patient", new CreateResourceRouteRequest(
            IngestionMode.ScheduledPull, null, patient.Id, "0 0 * * *", null, IsEnabled: true, Priority: 0,
            ResourceMappings: [new ResourceRouteMappingRequest(foreignEncounter.Id, true, 1)]),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*same source connection*");
    }

    private async Task<SourceConnectionDto> AddInteractiveEpicSourceAsync(string name = "Epic EHR Launch") =>
        await _sut.AddSourceConnectionAsync(new CreateSourceConnectionRequest(
            name, SourceSystemType.Epic, "https://fhir.epic.com/api/FHIR/R4",
            new SourceAuthenticationDto(AuthenticationType.None, "client-1", null, ["user/Patient.read"],
                null, null, null, null, null),
            ApplicationType.EhrLaunch,
            new SourceInteractiveConfigurationDto(["https://app/callback"], null,
                ["https://fhir.epic.com/interconnect-fhir-oauth/api/FHIR/R4"])),
            CancellationToken.None);

    private async Task<MappingProfileDto> AddMappingAsync(Guid sourceId, string resourceType) =>
        await _sut.AddMappingProfileAsync(new CreateMappingProfileRequest(
            $"{resourceType} -> SQL", resourceType, sourceId, Guid.NewGuid(), $"dbo.{resourceType}",
            [new MappingFieldDto("id", "$.id", MappingValueType.String, true, null, null)]),
            CancellationToken.None);
}

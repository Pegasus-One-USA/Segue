using FHIRBridge.Application.Abstractions.Mapping;
using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.Abstractions.Sources;
using FHIRBridge.Application.Services;
using FHIRBridge.Application.Validation;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Domain.Enums;
using FHIRBridge.Domain.ValueObjects;
using FHIRBridge.SharedKernel.Exceptions;
using FluentAssertions;
using Moq;

namespace FHIRBridge.UnitTests.Configuration;

/// <summary>
/// <see cref="ConfigurationService.GetMappingProfileUsageCountAsync"/> is the delete guard the Mapping Profiles
/// master screen relies on (docs/backend/14-mapping-profile-master-screen-plan.md §4.7) — it counts every route
/// that would trip the Restrict FK on <c>MappingProfileId</c> (route primary, a composite ResourceMappings entry,
/// or a parent reference target) before a delete is attempted, since the FK itself only surfaces as a raw DB error.
/// </summary>
public sealed class MappingProfileUsageAndDeleteTests
{
    private readonly Mock<IConfigurationRepository> _repository = new();
    private readonly ConfigurationService _sut;
    private readonly MappingProfile _mapping = new(
        "Patient → SQL", "Patient", Guid.NewGuid(), Guid.NewGuid(), "dbo.Patients", Array.Empty<MappingField>());

    public MappingProfileUsageAndDeleteTests()
    {
        _sut = new ConfigurationService(
            _repository.Object,
            Mock.Of<ISourceCapabilityRepository>(),
            Mock.Of<ISourceCapabilityDiscoveryService>(),
            Mock.Of<ISecretWriter>(),
            Mock.Of<FHIRBridge.Application.Abstractions.Security.ISecretProvider>(),
            Mock.Of<FHIRBridge.Application.Abstractions.Destinations.ISqlConnectionSecretMerger>(),
            new FHIRBridge.UnitTests.Security.PassthroughTenantSecretVaultResolver(),
            Mock.Of<IParentReferenceResolver>(),
            new CreateMappingProfileRequestValidator(
                new FHIRBridge.UnitTests.Validation.NoOpDestinationSchemaService(),
                _repository.Object,
                new FHIRBridge.UnitTests.Validation.NoOpEffectiveRuleResolver()),
            new CreateDestinationConfigurationRequestValidator(),
            new FHIRBridge.UnitTests.Security.PassthroughUserDisplayNameResolver(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ConfigurationService>.Instance);
    }

    private static ResourcePipelineRoute RouteWithPrimaryMapping(Guid mappingProfileId) =>
        new(null, mappingProfileId, IngestionMode.ScheduledPull, null, null, true, 0);

    [Fact]
    public async Task Usage_count_is_zero_when_no_route_references_the_mapping()
    {
        _repository.Setup(x => x.GetRoutesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([RouteWithPrimaryMapping(Guid.NewGuid())]);

        var count = await _sut.GetMappingProfileUsageCountAsync(_mapping.Id, CancellationToken.None);

        count.Should().Be(0);
    }

    [Fact]
    public async Task Usage_count_includes_a_route_whose_primary_mapping_matches()
    {
        _repository.Setup(x => x.GetRoutesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([RouteWithPrimaryMapping(_mapping.Id), RouteWithPrimaryMapping(Guid.NewGuid())]);

        var count = await _sut.GetMappingProfileUsageCountAsync(_mapping.Id, CancellationToken.None);

        count.Should().Be(1);
    }

    [Fact]
    public async Task Usage_count_includes_a_route_referencing_the_mapping_as_a_composite_resource_mapping()
    {
        var route = RouteWithPrimaryMapping(Guid.NewGuid());
        route.ReplaceResourceMappings([new ResourcePipelineRouteMapping(_mapping.Id, true, 0)]);
        _repository.Setup(x => x.GetRoutesAsync(It.IsAny<CancellationToken>())).ReturnsAsync([route]);

        var count = await _sut.GetMappingProfileUsageCountAsync(_mapping.Id, CancellationToken.None);

        count.Should().Be(1);
    }

    [Fact]
    public async Task Usage_count_includes_a_route_referencing_the_mapping_as_a_parent_reference_target()
    {
        var childMapping = new ResourcePipelineRouteMapping(Guid.NewGuid(), true, 0);
        childMapping.ReplaceParentReferences([new ParentReferenceLink(_mapping.Id)]);
        var route = RouteWithPrimaryMapping(Guid.NewGuid());
        route.ReplaceResourceMappings([childMapping]);
        _repository.Setup(x => x.GetRoutesAsync(It.IsAny<CancellationToken>())).ReturnsAsync([route]);

        var count = await _sut.GetMappingProfileUsageCountAsync(_mapping.Id, CancellationToken.None);

        count.Should().Be(1);
    }

    [Fact]
    public async Task Delete_removes_the_mapping_profile_when_it_exists()
    {
        _repository.Setup(x => x.GetMappingProfileAsync(_mapping.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(_mapping);

        await _sut.DeleteMappingProfileAsync(_mapping.Id, CancellationToken.None);

        _repository.Verify(x => x.RemoveMappingProfileAsync(_mapping, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Delete_throws_not_found_for_an_unknown_mapping_profile()
    {
        _repository.Setup(x => x.GetMappingProfileAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((MappingProfile?)null);

        var act = () => _sut.DeleteMappingProfileAsync(Guid.NewGuid(), CancellationToken.None);

        await act.Should().ThrowAsync<NotFoundException>();
    }
}

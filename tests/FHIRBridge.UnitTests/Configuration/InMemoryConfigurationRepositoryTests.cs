using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Domain.Enums;
using FHIRBridge.Domain.ValueObjects;
using FHIRBridge.Infrastructure.Persistence;
using FluentAssertions;

namespace FHIRBridge.UnitTests.Configuration;

/// <summary>
/// Round-trip coverage for the flat, tenant-free configuration store that replaced the Tenant aggregate.
/// Verifies add/get/update for the entity types services join on (source → mapping → route).
/// </summary>
public sealed class InMemoryConfigurationRepositoryTests
{
    private readonly InMemoryConfigurationRepository _repository = new();

    private static SourceConnection NewSource(string name = "Epic Backend") =>
        new(name, SourceSystemType.Epic, "https://fhir.example.com",
            new SourceAuthenticationConfiguration(
                AuthenticationType.SmartBackendServices, "client-1", "https://auth.example.com/token",
                ["system/*.read"], null, null, null));

    // ── Source connections ──────────────────────────────────────────────────────

    [Fact]
    public async Task Source_connection_add_then_get_round_trips_the_entity()
    {
        var source = NewSource();

        await _repository.AddSourceConnectionAsync(source, CancellationToken.None);
        var fetched = await _repository.GetSourceConnectionAsync(source.Id, CancellationToken.None);

        fetched.Should().NotBeNull();
        fetched!.Id.Should().Be(source.Id);
        fetched.Name.Should().Be("Epic Backend");
        fetched.SourceSystemType.Should().Be(SourceSystemType.Epic);
    }

    [Fact]
    public async Task Get_source_connection_returns_null_for_an_unknown_id()
    {
        (await _repository.GetSourceConnectionAsync(Guid.NewGuid(), CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task Update_source_connection_persists_the_mutation_without_creating_a_duplicate()
    {
        var source = NewSource();
        await _repository.AddSourceConnectionAsync(source, CancellationToken.None);

        source.Update("Epic Production", source.SourceSystemType, source.BaseUrl, source.Authentication);
        await _repository.UpdateSourceConnectionAsync(source, CancellationToken.None);

        var all = await _repository.GetSourceConnectionsAsync(CancellationToken.None);
        all.Should().ContainSingle();
        all[0].Name.Should().Be("Epic Production");
    }

    // ── Mapping profiles ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Mapping_profile_add_get_update_round_trips()
    {
        var sourceId = Guid.NewGuid();
        var destinationId = Guid.NewGuid();
        var mapping = new MappingProfile(
            "Observation → SQL", "Observation", sourceId, destinationId, "dbo.Observations", Array.Empty<MappingField>());

        await _repository.AddMappingProfileAsync(mapping, CancellationToken.None);

        var fetched = await _repository.GetMappingProfileAsync(mapping.Id, CancellationToken.None);
        fetched.Should().NotBeNull();
        fetched!.ResourceType.Should().Be("Observation");
        fetched.SourceConnectionId.Should().Be(sourceId);
        fetched.DestinationId.Should().Be(destinationId);

        mapping.Update("Observation → Warehouse", "Observation", sourceId, destinationId, "dbo.Observations", Array.Empty<MappingField>());
        await _repository.UpdateMappingProfileAsync(mapping, CancellationToken.None);

        var all = await _repository.GetMappingProfilesAsync(CancellationToken.None);
        all.Should().ContainSingle().Which.Name.Should().Be("Observation → Warehouse");
    }

    [Fact]
    public async Task Mapping_profile_remove_deletes_it_from_the_store()
    {
        var mapping = new MappingProfile(
            "Patient → SQL", "Patient", Guid.NewGuid(), Guid.NewGuid(), "dbo.Patients", Array.Empty<MappingField>());
        await _repository.AddMappingProfileAsync(mapping, CancellationToken.None);

        await _repository.RemoveMappingProfileAsync(mapping, CancellationToken.None);

        (await _repository.GetMappingProfileAsync(mapping.Id, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task Mapping_profiles_paged_filters_by_resource_type_source_destination_and_enabled()
    {
        var sourceId = Guid.NewGuid();
        var destinationId = Guid.NewGuid();
        var patient = new MappingProfile("Patient → SQL", "Patient", sourceId, destinationId, "dbo.Patients", Array.Empty<MappingField>());
        var observation = new MappingProfile("Observation → SQL", "Observation", sourceId, destinationId, "dbo.Observations", Array.Empty<MappingField>());
        var otherSource = new MappingProfile("Patient → Other", "Patient", Guid.NewGuid(), destinationId, "dbo.Patients", Array.Empty<MappingField>());
        observation.SetEnabled(false);
        await _repository.AddMappingProfileAsync(patient, CancellationToken.None);
        await _repository.AddMappingProfileAsync(observation, CancellationToken.None);
        await _repository.AddMappingProfileAsync(otherSource, CancellationToken.None);

        var byResourceType = await _repository.GetMappingProfilesPagedAsync(
            new MappingProfileFilter(null, "Patient", null, null, null), 1, 25, null, null, CancellationToken.None);
        byResourceType.Items.Should().BeEquivalentTo([patient, otherSource]);

        var bySource = await _repository.GetMappingProfilesPagedAsync(
            new MappingProfileFilter(null, null, sourceId, null, null), 1, 25, null, null, CancellationToken.None);
        bySource.Items.Should().BeEquivalentTo([patient, observation]);

        var byEnabled = await _repository.GetMappingProfilesPagedAsync(
            new MappingProfileFilter(null, null, null, null, false), 1, 25, null, null, CancellationToken.None);
        byEnabled.Items.Should().ContainSingle().Which.Id.Should().Be(observation.Id);
        byEnabled.TotalCount.Should().Be(1);
    }

    [Fact]
    public async Task Mapping_profiles_paged_sorts_by_resource_type_and_falls_back_to_name_for_an_unknown_column()
    {
        var patient = new MappingProfile("B Mapping", "Patient", Guid.NewGuid(), Guid.NewGuid(), "dbo.Patients", Array.Empty<MappingField>());
        var observation = new MappingProfile("A Mapping", "Observation", Guid.NewGuid(), Guid.NewGuid(), "dbo.Observations", Array.Empty<MappingField>());
        await _repository.AddMappingProfileAsync(patient, CancellationToken.None);
        await _repository.AddMappingProfileAsync(observation, CancellationToken.None);

        var byResourceType = await _repository.GetMappingProfilesPagedAsync(
            new MappingProfileFilter(null, null, null, null, null), 1, 25, "resourceType", "desc", CancellationToken.None);
        byResourceType.Items.Select(x => x.ResourceType).Should().Equal("Patient", "Observation");

        var byUnknownColumn = await _repository.GetMappingProfilesPagedAsync(
            new MappingProfileFilter(null, null, null, null, null), 1, 25, "not-a-real-column", null, CancellationToken.None);
        byUnknownColumn.Items.Select(x => x.Name).Should().Equal("A Mapping", "B Mapping");
    }

    [Fact]
    public async Task Mapping_profiles_paged_pages_results()
    {
        for (var i = 0; i < 3; i++)
        {
            await _repository.AddMappingProfileAsync(
                new MappingProfile($"Mapping {i}", "Patient", Guid.NewGuid(), Guid.NewGuid(), "dbo.Patients", Array.Empty<MappingField>()),
                CancellationToken.None);
        }

        var firstPage = await _repository.GetMappingProfilesPagedAsync(
            new MappingProfileFilter(null, null, null, null, null), 1, 2, null, null, CancellationToken.None);
        var secondPage = await _repository.GetMappingProfilesPagedAsync(
            new MappingProfileFilter(null, null, null, null, null), 2, 2, null, null, CancellationToken.None);

        firstPage.Items.Should().HaveCount(2);
        firstPage.TotalCount.Should().Be(3);
        secondPage.Items.Should().ContainSingle();
    }

    // ── Resource pipeline routes ──────────────────────────────────────────────────

    [Fact]
    public async Task Route_add_get_update_round_trips()
    {
        var mappingId = Guid.NewGuid();
        var route = new ResourcePipelineRoute(
            null, mappingId, IngestionMode.ScheduledPull, "0 0 * * *", null, true, 1);

        await _repository.AddRouteAsync(route, CancellationToken.None);

        var fetched = await _repository.GetRouteAsync(route.Id, CancellationToken.None);
        fetched.Should().NotBeNull();
        fetched!.MappingProfileId.Should().Be(mappingId);
        fetched.IngestionMode.Should().Be(IngestionMode.ScheduledPull);
        fetched.IsEnabled.Should().BeTrue();

        route.SetEnabled(false);
        await _repository.UpdateRouteAsync(route, CancellationToken.None);

        var all = await _repository.GetRoutesAsync(CancellationToken.None);
        all.Should().ContainSingle().Which.IsEnabled.Should().BeFalse();
    }
}

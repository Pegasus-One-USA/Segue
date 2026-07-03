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

using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Domain.Enums;
using FHIRBridge.Domain.ValueObjects;
using FHIRBridge.Infrastructure.Sources;
using FluentAssertions;
using Moq;

namespace FHIRBridge.UnitTests.Sources;

public sealed class SourceConnectionSyncCursorStoreTests
{
    private static readonly SourceAuthenticationConfiguration Auth =
        new(AuthenticationType.OAuthClientCredentials, "client-1", null, ["system/Patient.rs", "system/Observation.rs"], null, null, null);

    [Fact]
    public async Task Advances_only_the_given_resource_types_in_a_single_repository_round_trip()
    {
        var retrieval = new SourceRetrievalConfiguration(
            "search-rest", ["Patient", "Observation"], null, incrementalSyncEnabled: true);
        var connection = new SourceConnection(
            "Epic Backend", SourceSystemType.Epic, "https://fhir.example.com", Auth, retrieval: retrieval);

        var repository = new Mock<IConfigurationRepository>();
        repository.Setup(x => x.GetSourceConnectionAsync(connection.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(connection);

        var store = new SourceConnectionSyncCursorStore(repository.Object);
        var syncedAt = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc);

        await store.RecordSuccessfulSyncAsync(connection.Id, ["Patient"], syncedAt, CancellationToken.None);

        connection.Retrieval!.GetLastSuccessfulSyncUtc("Patient").Should().Be(syncedAt);
        connection.Retrieval!.GetLastSuccessfulSyncUtc("Observation").Should().BeNull();
        repository.Verify(x => x.GetSourceConnectionAsync(connection.Id, It.IsAny<CancellationToken>()), Times.Once);
        repository.Verify(x => x.UpdateSourceConnectionAsync(connection, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Does_nothing_when_no_resource_types_synced()
    {
        var repository = new Mock<IConfigurationRepository>();
        var store = new SourceConnectionSyncCursorStore(repository.Object);

        await store.RecordSuccessfulSyncAsync(Guid.NewGuid(), [], DateTime.UtcNow, CancellationToken.None);

        repository.Verify(x => x.GetSourceConnectionAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Does_nothing_when_the_connection_no_longer_exists()
    {
        var repository = new Mock<IConfigurationRepository>();
        repository.Setup(x => x.GetSourceConnectionAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SourceConnection?)null);

        var store = new SourceConnectionSyncCursorStore(repository.Object);

        await store.RecordSuccessfulSyncAsync(Guid.NewGuid(), ["Patient"], DateTime.UtcNow, CancellationToken.None);

        repository.Verify(x => x.UpdateSourceConnectionAsync(It.IsAny<SourceConnection>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}

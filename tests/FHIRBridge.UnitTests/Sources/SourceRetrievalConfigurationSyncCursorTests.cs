using FHIRBridge.Domain.Entities;
using FHIRBridge.Domain.Enums;
using FHIRBridge.Domain.ValueObjects;
using FluentAssertions;

namespace FHIRBridge.UnitTests.Sources;

// Each resource type on a Backend System source connection is fetched via its own independent search request, so
// each must track its own incremental-sync ("_lastUpdated") watermark rather than sharing one connection-wide value —
// see SourceRetrievalConfiguration.LastSuccessfulSyncUtcByResourceType.
public sealed class SourceRetrievalConfigurationSyncCursorTests
{
    private static readonly SourceAuthenticationConfiguration Auth =
        new(AuthenticationType.OAuthClientCredentials, "client-1", null, ["system/Patient.rs", "system/Observation.rs"], null, null, null);

    [Fact]
    public void WithLastSuccessfulSync_only_advances_the_given_resource_types()
    {
        var retrieval = new SourceRetrievalConfiguration(
            "search-rest", ["Patient", "Observation"], null, incrementalSyncEnabled: true);
        var firstSync = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        var afterPatientOnly = retrieval.WithLastSuccessfulSync(["Patient"], firstSync);

        afterPatientOnly.GetLastSuccessfulSyncUtc("Patient").Should().Be(firstSync);
        afterPatientOnly.GetLastSuccessfulSyncUtc("Observation").Should().BeNull();

        var secondSync = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc);
        var afterBoth = afterPatientOnly.WithLastSuccessfulSync(["Observation"], secondSync);

        // Patient's cursor from the first call is untouched by the second call recording Observation only.
        afterBoth.GetLastSuccessfulSyncUtc("Patient").Should().Be(firstSync);
        afterBoth.GetLastSuccessfulSyncUtc("Observation").Should().Be(secondSync);
    }

    [Fact]
    public void SourceConnection_RecordRetrievalSync_is_a_noop_without_a_retrieval_configuration()
    {
        var connection = new SourceConnection("Epic Backend", SourceSystemType.Epic, "https://fhir.example.com", Auth);

        connection.RecordRetrievalSync(["Patient"], DateTime.UtcNow);

        connection.Retrieval.Should().BeNull();
    }

    [Fact]
    public void SourceConnection_RecordRetrievalSync_advances_only_the_synced_types()
    {
        var retrieval = new SourceRetrievalConfiguration(
            "search-rest", ["Patient", "Observation"], null, incrementalSyncEnabled: true);
        var connection = new SourceConnection(
            "Epic Backend", SourceSystemType.Epic, "https://fhir.example.com", Auth, retrieval: retrieval);
        var syncedAt = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);

        connection.RecordRetrievalSync(["Patient"], syncedAt);

        connection.Retrieval!.GetLastSuccessfulSyncUtc("Patient").Should().Be(syncedAt);
        connection.Retrieval!.GetLastSuccessfulSyncUtc("Observation").Should().BeNull();
    }

    [Fact]
    public void GetEarliestSuccessfulSyncUtc_returns_null_when_any_requested_type_has_never_synced()
    {
        var retrieval = new SourceRetrievalConfiguration(
            "bulk-export", ["Patient", "Observation"], null, incrementalSyncEnabled: true,
            lastSuccessfulSyncUtcByResourceType: new Dictionary<string, DateTime>
            {
                ["Patient"] = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            });

        retrieval.GetEarliestSuccessfulSyncUtc(["Patient", "Observation"]).Should().BeNull();
        retrieval.GetEarliestSuccessfulSyncUtc(["Patient"]).Should().Be(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void GetEarliestSuccessfulSyncUtc_returns_the_minimum_across_types_when_all_have_synced()
    {
        var earlier = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var later = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var retrieval = new SourceRetrievalConfiguration(
            "bulk-export", ["Patient", "Observation"], null, incrementalSyncEnabled: true,
            lastSuccessfulSyncUtcByResourceType: new Dictionary<string, DateTime>
            {
                ["Patient"] = later,
                ["Observation"] = earlier,
            });

        retrieval.GetEarliestSuccessfulSyncUtc(["Patient", "Observation"]).Should().Be(earlier);
    }
}

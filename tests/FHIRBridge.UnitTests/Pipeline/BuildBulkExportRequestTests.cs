using FHIRBridge.Domain.ValueObjects;
using FHIRBridge.Infrastructure.Pipeline;
using FHIRBridge.Runtime.Application.DTOs;
using FHIRBridge.Runtime.Domain.Enums;
using FluentAssertions;

namespace FHIRBridge.UnitTests.Pipeline;

/// <summary>
/// The configured pipeline projects a Backend System source's persisted bulk-export retrieval settings onto a runtime
/// <c>$export</c> request. These cover that projection: scope selection, group/patient narrowing, the <c>_since</c>
/// cursor gate, and the safe System default for legacy/unset configurations.
/// </summary>
public sealed class BuildBulkExportRequestTests
{
    private static SourceRetrievalConfiguration Bulk(
        string? exportScope = null,
        string? groupId = null,
        string[]? patientIds = null,
        bool incremental = false,
        DateTime? lastSync = null,
        string? outputFormat = null) =>
        new(
            retrievalMethod: "bulk-export",
            resourceTypes: ["Patient"],
            searchCriteria: null,
            incrementalSyncEnabled: incremental,
            lastSuccessfulSyncUtcByResourceType: lastSync is { } syncedAt
                ? new Dictionary<string, DateTime> { ["Patient"] = syncedAt }
                : null,
            exportScope: exportScope,
            groupId: groupId,
            patientIds: patientIds,
            outputFormat: outputFormat);

    [Fact]
    public void Null_retrieval_defaults_to_system_scope_for_the_requested_resource_type()
    {
        var request = ConfiguredPipelineService.BuildBulkExportRequest(null, "Observation");

        request.Scope.Should().Be(BulkExportScope.System);
        request.ResourceTypes.Should().ContainSingle().Which.Should().Be("Observation");
        request.GroupId.Should().BeNull();
        request.PatientIds.Should().BeNull();
        request.Since.Should().BeNull();
    }

    [Theory]
    [InlineData("system", BulkExportScope.System)]
    [InlineData("group", BulkExportScope.Group)]
    [InlineData("patient", BulkExportScope.Patient)]
    [InlineData("GROUP", BulkExportScope.Group)]
    [InlineData("unknown", BulkExportScope.System)]
    [InlineData(null, BulkExportScope.System)]
    public void Scope_token_maps_case_insensitively_with_system_fallback(string? token, BulkExportScope expected)
    {
        ConfiguredPipelineService.MapBulkExportScope(token).Should().Be(expected);
    }

    [Fact]
    public void Group_scope_carries_group_id_and_omits_patient_ids()
    {
        var request = ConfiguredPipelineService.BuildBulkExportRequest(
            Bulk(exportScope: "group", groupId: "grp-1", patientIds: ["p1"]), "Patient");

        request.Scope.Should().Be(BulkExportScope.Group);
        request.GroupId.Should().Be("grp-1");
        request.PatientIds.Should().BeNull();
    }

    [Fact]
    public void Patient_scope_carries_patient_ids_and_omits_group_id()
    {
        var request = ConfiguredPipelineService.BuildBulkExportRequest(
            Bulk(exportScope: "patient", groupId: "grp-1", patientIds: ["p1", "p2"]), "Patient");

        request.Scope.Should().Be(BulkExportScope.Patient);
        request.PatientIds.Should().Equal("p1", "p2");
        request.GroupId.Should().BeNull();
    }

    [Fact]
    public void Since_is_applied_only_when_incremental_and_a_prior_sync_exists()
    {
        var syncedAt = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);

        ConfiguredPipelineService.BuildBulkExportRequest(Bulk(incremental: true, lastSync: syncedAt), "Patient")
            .Since.Should().Be(new DateTimeOffset(syncedAt));

        // Incremental off → no cursor even if a timestamp is present.
        ConfiguredPipelineService.BuildBulkExportRequest(Bulk(incremental: false, lastSync: syncedAt), "Patient")
            .Since.Should().BeNull();

        // Incremental on but no prior successful run → no cursor.
        ConfiguredPipelineService.BuildBulkExportRequest(Bulk(incremental: true, lastSync: null), "Patient")
            .Since.Should().BeNull();
    }

    [Fact]
    public void Output_format_is_passed_through()
    {
        var request = ConfiguredPipelineService.BuildBulkExportRequest(
            Bulk(exportScope: "system", outputFormat: "application/fhir+ndjson"), "Patient");

        request.OutputFormat.Should().Be("application/fhir+ndjson");
    }

    [Fact]
    public void Non_athenahealth_group_id_passes_through_unchanged()
    {
        var request = ConfiguredPipelineService.BuildBulkExportRequest(
            Bulk(exportScope: "group", groupId: "195900"), "Patient", RuntimeSourceType.Epic, practiceId: null);

        request.GroupId.Should().Be("195900");
    }

    [Fact]
    public void Athenahealth_bare_numeric_group_id_is_reformatted_to_practice_reference()
    {
        var request = ConfiguredPipelineService.BuildBulkExportRequest(
            Bulk(exportScope: "group", groupId: "195900"), "Patient", RuntimeSourceType.Athenahealth, practiceId: null);

        request.GroupId.Should().Be("a-1.C-195900");
    }

    [Fact]
    public void Athenahealth_blank_group_id_falls_back_to_the_connections_practice_id()
    {
        var request = ConfiguredPipelineService.BuildBulkExportRequest(
            Bulk(exportScope: "group", groupId: null), "Patient", RuntimeSourceType.Athenahealth, practiceId: "195900");

        request.GroupId.Should().Be("a-1.C-195900");
    }

    [Fact]
    public void Athenahealth_already_formatted_group_id_is_left_untouched()
    {
        var request = ConfiguredPipelineService.BuildBulkExportRequest(
            Bulk(exportScope: "group", groupId: "a-1.C-195900"), "Patient", RuntimeSourceType.Athenahealth, practiceId: null);

        request.GroupId.Should().Be("a-1.C-195900");
    }
}

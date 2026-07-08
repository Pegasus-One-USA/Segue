namespace FHIRBridge.Application.DTOs;

/// <summary>Decrypted, drill-down view of one resource's fetch/normalize/map/store history within a route execution.</summary>
public sealed record PipelineRunResourceHistoryDto(
    Guid Id,
    Guid RouteExecutionId,
    string ResourceType,
    string? SourceResourceId,
    string Stage,
    string? ErrorMessage,
    string FetchedJson,
    DateTime FetchedAtUtc,
    string? NormalizedJson,
    IReadOnlyList<string> AppliedProfiles,
    IReadOnlyList<string> Warnings,
    double? DataQualityScore,
    string? MasterPatientId,
    DateTime? NormalizedAtUtc,
    string? MappedValuesJson,
    DateTime? MappedAtUtc,
    DateTime? StoredAtUtc,
    string? WriteStatus);

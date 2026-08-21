namespace FHIRBridge.Application.DTOs;

/// <summary>
/// PHI-free, drill-down view of one resource's fetch/normalize/map/store history within a route execution.
/// The raw fetched/normalized/mapped JSON payloads are deliberately NOT exposed here — this list screen only
/// needs the per-stage status, timing, warnings, and data-quality metadata. To view an individual decrypted
/// field value, use the gated + audited reveal on the Data Lineage screen (see IDataLineageService), which
/// requires the stricter Payload/View permission and writes a DataAccessLog per reveal.
/// </summary>
public sealed record PipelineRunResourceHistoryDto(
    Guid Id,
    Guid RouteExecutionId,
    string ResourceType,
    string? SourceResourceId,
    string Stage,
    string? ErrorMessage,
    DateTime FetchedAtUtc,
    IReadOnlyList<string> AppliedProfiles,
    IReadOnlyList<string> Warnings,
    double? DataQualityScore,
    string? MasterPatientId,
    DateTime? NormalizedAtUtc,
    DateTime? MappedAtUtc,
    DateTime? StoredAtUtc,
    string? WriteStatus);

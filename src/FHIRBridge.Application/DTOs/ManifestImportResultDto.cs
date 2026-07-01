namespace FHIRBridge.Application.DTOs;

/// <summary>
/// Summary of a successful YAML manifest import: the tenant that was created plus a count of each
/// configuration entity materialized. <see cref="Warnings"/> carries non-fatal notes (e.g. references that
/// were skipped) so the caller can surface them without failing the import.
/// </summary>
public sealed record ManifestImportResultDto(
    Guid TenantId,
    string TenantName,
    string TenantCode,
    int SourceCount,
    int DestinationCount,
    int WebhookCount,
    int MappingProfileCount,
    int ResourceCount,
    int RouteCount,
    IReadOnlyList<string> Warnings);

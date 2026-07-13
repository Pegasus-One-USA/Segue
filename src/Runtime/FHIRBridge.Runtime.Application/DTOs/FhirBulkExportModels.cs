namespace FHIRBridge.Runtime.Application.DTOs;

/// <summary>The scope of a FHIR Bulk Data export ($export) kick-off.</summary>
public enum BulkExportScope
{
    /// <summary>System-level export: <c>[base]/$export</c>.</summary>
    System = 0,

    /// <summary>All patients: <c>[base]/Patient/$export</c>.</summary>
    Patient = 1,

    /// <summary>A group's members: <c>[base]/Group/{id}/$export</c>.</summary>
    Group = 2
}

/// <summary>Describes a FHIR Bulk Data <c>$export</c> request.</summary>
public sealed record FhirBulkExportRequest(
    BulkExportScope Scope = BulkExportScope.Patient,
    string? GroupId = null,
    IReadOnlyCollection<string>? ResourceTypes = null,
    DateTimeOffset? Since = null,
    string? TypeFilter = null,
    IReadOnlyCollection<string>? PatientIds = null,
    string? OutputFormat = null);

/// <summary>One NDJSON output file produced by a completed export.</summary>
public sealed record BulkExportFile(string ResourceType, string Url);

/// <summary>Shared parsing of the persisted export-scope token to <see cref="BulkExportScope"/> (System is the safe
/// default for unset/legacy/unknown values). Used by both pipeline planes so the mapping never diverges.</summary>
public static class BulkExportScopes
{
    public static BulkExportScope Parse(string? exportScope) => exportScope?.Trim().ToLowerInvariant() switch
    {
        "group" => BulkExportScope.Group,
        "patient" => BulkExportScope.Patient,
        _ => BulkExportScope.System,
    };
}

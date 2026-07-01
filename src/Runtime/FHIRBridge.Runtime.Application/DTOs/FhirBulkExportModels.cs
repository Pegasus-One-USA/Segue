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
    string? TypeFilter = null);

/// <summary>One NDJSON output file produced by a completed export.</summary>
public sealed record BulkExportFile(string ResourceType, string Url);

namespace FHIRBridge.Application.DTOs;

/// <summary>One credential field a HAPI terminology system's Edit form may need (e.g. LOINC's username
/// and password, or SNOMED CT/RxNorm's shared UTS API key). Write-only, same convention as the legacy
/// Loinc/Snomed/RxNorm configuration DTOs: the value itself is never returned, only whether one is set.</summary>
public sealed record HapiCredentialFieldDto(string Name, string Label, bool HasValue);

/// <summary>Safe, operator-facing configuration for one HAPI-terminology-server sync system. Secret
/// values are never returned.</summary>
public sealed record HapiTerminologyConfigurationDto(
    string Code,
    string DisplayName,
    bool SchedulerEnabled,
    string Frequency,
    IReadOnlyList<string> FrequencyOptions,
    string ExecutionTime,
    DateTime? LastRunUtc,
    IReadOnlyList<HapiCredentialFieldDto> Credentials,
    /// <summary>Non-null only for the systems whose HAPI sync actually reads a configurable download
    /// endpoint (currently LOINC only) — see HapiTerminologySystemRegistry's DownloadApiUrlSettingKey.
    /// Unlike Credentials this is a plain visible value, not write-only.</summary>
    string? DownloadApiUrl = null);

/// <summary>Updates one HAPI-terminology-server sync system's settings in a single call. Omit a
/// credential value (or leave it blank) to keep the provisioned one unchanged.</summary>
public sealed record UpdateHapiTerminologyConfigurationRequest(
    bool SchedulerEnabled,
    string Frequency,
    string ExecutionTime,
    IReadOnlyDictionary<string, string>? CredentialValues,
    string? DownloadApiUrl = null);

/// <summary>One row in a HAPI terminology system's run history — shape matches the portal's shared
/// TerminologyImportHistoryEntry interface exactly, so the existing history table component needs no
/// changes to render it.</summary>
public sealed record HapiTerminologyImportHistoryEntryDto(
    Guid Id,
    string? Version,
    DateTime StartedOnUtc,
    DateTime? CompletedOnUtc,
    int ImportedConceptCount,
    string Status,
    string? ErrorMessage);

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

/// <summary>Result of checking one HAPI terminology system's source for a newer version than what's
/// currently stored locally, without downloading/importing anything. <see cref="Supported"/> is false
/// for the systems whose source has no discoverable "latest version" pointer to check (see
/// HapiTerminologySystemRegistry.CheckLatestVersionAsync) — in that case StoredVersion is still
/// populated but LatestAvailableVersion is always null and UpdateAvailable is always false.</summary>
public sealed record HapiTerminologyVersionCheckResultDto(
    string Code,
    bool Supported,
    string? StoredVersion,
    string? LatestAvailableVersion,
    bool UpdateAvailable,
    string? ErrorMessage);

/// <summary>One code/description row from a HAPI terminology system's local store (TRM_CONCEPT),
/// browsable/editable from Settings → General → Terminology's "View All Codes" screen.</summary>
public sealed record TerminologyConceptDto(
    long Pid,
    string Code,
    string? Display,
    string? ShortDescription,
    string? LongDescription,
    string? LongCommonName,
    bool IsActive);

/// <summary>Adds or edits one code/description row for a given HAPI terminology system, stored the
/// same way (TRM_CODESYSTEM/TRM_CODESYSTEM_VER/TRM_CONCEPT) as a synced code, e.g. ICD-10-CM.</summary>
public sealed record UpsertTerminologyConceptRequest(
    string Code,
    string? Display,
    string? ShortDescription = null,
    string? LongDescription = null,
    string? LongCommonName = null,
    // Defaulted true so an older client that posts only Code+Display still creates an active concept,
    // matching the rule applied to sources that publish no status signal.
    bool IsActive = true);

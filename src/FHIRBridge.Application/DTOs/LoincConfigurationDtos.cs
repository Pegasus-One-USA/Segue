namespace FHIRBridge.Application.DTOs;

/// <summary>Safe operator-facing LOINC configuration. Secret values are never returned.</summary>
public sealed record LoincConfigurationDto(
    string DownloadApiUrl,
    string FhirApiUrl,
    string UsernameSecretName,
    string PasswordSecretName,
    bool HasUsernameConfigured,
    bool HasPasswordConfigured,
    bool SchedulerEnabled,
    string Frequency,
    string ExecutionTime,
    int RetryCount,
    int RetryIntervalSeconds,
    int DownloadTimeoutSeconds);

/// <summary>
/// Updates LOINC configuration. Omit a credential to leave the provisioned value unchanged; values are write-only.
/// </summary>
public sealed record UpdateLoincConfigurationRequest(
    string DownloadApiUrl,
    string? FhirApiUrl,
    string? Username,
    string? Password,
    bool SchedulerEnabled,
    string Frequency,
    string ExecutionTime,
    int RetryCount,
    int RetryIntervalSeconds,
    int DownloadTimeoutSeconds);

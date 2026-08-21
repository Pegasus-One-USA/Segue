namespace FHIRBridge.Application.DTOs;

/// <summary>Safe operator-facing SNOMED CT auto-sync configuration. The API key is never returned.</summary>
public sealed record SnomedConfigurationDto(
    bool HasApiKeyConfigured,
    bool SchedulerEnabled,
    string ExecutionTime,
    int RetryCount,
    int RetryIntervalSeconds);

/// <summary>Updates SNOMED CT auto-sync configuration. Omit ApiKey to leave the provisioned value unchanged.</summary>
public sealed record UpdateSnomedConfigurationRequest(
    string? ApiKey,
    bool SchedulerEnabled,
    string ExecutionTime,
    int RetryCount,
    int RetryIntervalSeconds);

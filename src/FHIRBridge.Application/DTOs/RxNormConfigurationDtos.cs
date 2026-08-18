namespace FHIRBridge.Application.DTOs;

/// <summary>Safe operator-facing RxNorm auto-sync configuration. The API key is never returned.</summary>
public sealed record RxNormConfigurationDto(
    bool HasApiKeyConfigured,
    bool SchedulerEnabled,
    string ExecutionTime,
    int RetryCount,
    int RetryIntervalSeconds);

/// <summary>Updates RxNorm auto-sync configuration. Omit ApiKey to leave the provisioned value unchanged.</summary>
public sealed record UpdateRxNormConfigurationRequest(
    string? ApiKey,
    bool SchedulerEnabled,
    string ExecutionTime,
    int RetryCount,
    int RetryIntervalSeconds);

namespace FHIRBridge.Application.DTOs;

/// <summary>Safe operator-facing NDC auto-sync configuration. The optional API key is never returned.</summary>
public sealed record NdcConfigurationDto(
    bool HasApiKeyConfigured,
    bool SchedulerEnabled,
    string Frequency,
    string ExecutionTime);

/// <summary>Updates NDC auto-sync configuration. ApiKey is optional — openFDA works without one; providing it
/// only raises the caller's rate limit. Omit to leave the provisioned value unchanged.</summary>
public sealed record UpdateNdcConfigurationRequest(
    string? ApiKey,
    bool SchedulerEnabled,
    string Frequency,
    string ExecutionTime);

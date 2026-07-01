namespace FHIRBridge.Application.DTOs;

public sealed record WebhookConfigurationDto(
    Guid Id,
    Guid SourceConnectionId,
    string ResourceType,
    string Name,
    string Path,
    bool IsEnabled);

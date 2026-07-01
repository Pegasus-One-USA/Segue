namespace FHIRBridge.Application.DTOs;

public sealed record CreateWebhookConfigurationRequest(
    Guid SourceConnectionId,
    string ResourceType,
    string Name,
    string Path,
    bool IsEnabled);

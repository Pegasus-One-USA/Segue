using FHIRBridge.Application.DTOs;

namespace FHIRBridge.Application.Abstractions.Pipeline;

public interface IConfiguredPipelineService
{
    Task<ConfiguredPipelineRunDto> StartAsync(
        Guid tenantId,
        StartConfiguredPipelineRunRequest request,
        CancellationToken cancellationToken);

    Task<ConfiguredPipelineRunDto> StartWebhookAsync(
        Guid tenantId,
        Guid webhookConfigurationId,
        WebhookIngestionRequest request,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<ConfiguredPipelineRunDto>> GetRecentAsync(
        Guid tenantId,
        int count,
        CancellationToken cancellationToken);

    Task SetRunEnabledAsync(
        Guid tenantId,
        Guid pipelineRunId,
        bool isEnabled,
        CancellationToken cancellationToken);
}

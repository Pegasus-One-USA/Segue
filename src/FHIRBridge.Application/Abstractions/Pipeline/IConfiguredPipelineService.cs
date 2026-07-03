using FHIRBridge.Application.DTOs;

namespace FHIRBridge.Application.Abstractions.Pipeline;

public interface IConfiguredPipelineService
{
    Task<ConfiguredPipelineRunDto> StartAsync(
        StartConfiguredPipelineRunRequest request,
        CancellationToken cancellationToken);

    Task<ConfiguredPipelineRunDto> StartWebhookAsync(
        Guid webhookConfigurationId,
        WebhookIngestionRequest request,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<ConfiguredPipelineRunDto>> GetRecentAsync(
        int count,
        CancellationToken cancellationToken);

    Task SetRunEnabledAsync(
        Guid pipelineRunId,
        bool isEnabled,
        CancellationToken cancellationToken);
}

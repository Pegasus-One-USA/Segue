using FHIRBridge.Application.Abstractions.Messaging;

namespace FHIRBridge.Infrastructure.Messaging;

/// <summary>No shared transport (Messaging:Provider=InMemory) — there is no real queue to report on.</summary>
public sealed class NullQueueMonitorProvider : IQueueMonitorProvider
{
    public Task<IReadOnlyList<QueueDepthDto>> GetQueueDepthsAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<QueueDepthDto> result =
        [
            new QueueDepthDto("pipeline-runs", "InMemory", 0, 0, 0, null,
                "No shared message transport configured (Messaging:Provider=InMemory) — nothing to monitor."),
            new QueueDepthDto("webhook-ingestion", "InMemory", 0, 0, 0, null,
                "No shared message transport configured (Messaging:Provider=InMemory) — nothing to monitor."),
        ];

        return Task.FromResult(result);
    }
}

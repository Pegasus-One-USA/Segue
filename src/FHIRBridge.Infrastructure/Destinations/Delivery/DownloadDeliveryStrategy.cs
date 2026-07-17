using FHIRBridge.Application.Abstractions.Destinations;
using FHIRBridge.Domain.Entities;

namespace FHIRBridge.Infrastructure.Destinations.Delivery;

/// <summary>
/// Hands the generated file straight back as the pipeline run's inline download — no persistence, no secret. Only
/// valid when <see cref="PipelineWriteContext.AllowInlineDelivery"/> is true, i.e. the run was started synchronously
/// by <c>PipelineRunsController.Start</c>'s non-bulk-export branch, the one call path with an HTTP response to carry
/// bytes back through. The wizard is expected to disable this mode for scheduled/webhook-triggered destinations —
/// this throw is the defense-in-depth backstop for any path that bypasses that UI guard.
/// </summary>
public sealed class DownloadDeliveryStrategy : IArtifactDeliveryStrategy
{
    public Task<DestinationWriteResult> DeliverAsync(
        DestinationConfiguration destination,
        GeneratedFile file,
        int recordCount,
        PipelineWriteContext context,
        CancellationToken cancellationToken)
    {
        if (!context.AllowInlineDelivery)
        {
            throw new InvalidOperationException(
                "This destination is configured for Download delivery, which requires a synchronous, manually " +
                "triggered pipeline run. It cannot be used from a scheduled, webhook, or bulk-export run.");
        }

        return Task.FromResult(new DestinationWriteResult(recordCount, InlineDownload: file));
    }
}

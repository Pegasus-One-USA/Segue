using FHIRBridge.Domain.Entities;

namespace FHIRBridge.Application.Abstractions.Destinations;

/// <summary>
/// Delivers an already-generated file somewhere (download, email, SFTP, or a download link), keyed by
/// <see cref="Domain.Enums.ArtifactDeliveryMode"/>. Deliberately format-agnostic: it never inspects
/// <paramref name="file"/>'s content, only its bytes/name/content-type, so every format writer can reuse the same
/// four strategies without new per-format classes.
/// </summary>
public interface IArtifactDeliveryStrategy
{
    Task<DestinationWriteResult> DeliverAsync(
        DestinationConfiguration destination,
        GeneratedFile file,
        int recordCount,
        PipelineWriteContext context,
        CancellationToken cancellationToken);
}

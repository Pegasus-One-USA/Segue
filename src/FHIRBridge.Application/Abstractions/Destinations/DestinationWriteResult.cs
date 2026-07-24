using FHIRBridge.Domain.Enums;

namespace FHIRBridge.Application.Abstractions.Destinations;

/// <summary>
/// Outcome of a single destination write. <see cref="InlineDownload"/> and <see cref="DownloadUrl"/> are populated
/// only by the (currently CSV-only) <see cref="ArtifactDeliveryMode.Download"/>/<see cref="ArtifactDeliveryMode.DownloadUrl"/>
/// delivery modes; every other writer/mode leaves them null and this is just a count, exactly as before.
/// </summary>
public sealed record DestinationWriteResult(
    int Count,
    GeneratedFile? InlineDownload = null,
    string? DownloadUrl = null);

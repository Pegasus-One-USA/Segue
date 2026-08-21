namespace FHIRBridge.Application.Abstractions.Messaging;

/// <summary>
/// Reports real queue depth for the configured messaging transport — one implementation per <c>Messaging:Provider</c>
/// value (registry over switch), never a fabricated result. If the transport can't be queried (no shared transport
/// configured, or the admin API/client call itself fails), <see cref="QueueDepthDto.UnavailableReason"/> is set and
/// the caller must show that explicitly rather than rendering empty/zeroed rows as if they were real.
/// </summary>
public interface IQueueMonitorProvider
{
    Task<IReadOnlyList<QueueDepthDto>> GetQueueDepthsAsync(CancellationToken cancellationToken);
}

public sealed record QueueDepthDto(
    string QueueName,
    string TransportType,
    int Pending,
    int Processing,
    int DeadLetter,
    DateTime? LastMessageUtc,
    string? UnavailableReason);

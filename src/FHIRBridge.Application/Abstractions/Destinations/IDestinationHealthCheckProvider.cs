using FHIRBridge.Domain.Entities;
using FHIRBridge.Domain.Enums;

namespace FHIRBridge.Application.Abstractions.Destinations;

/// <summary>
/// One health-check strategy per destination type — registry over switch, mirroring the source-connection axis's
/// vendor-subclass pattern. Not every <see cref="DestinationType"/> has a registered provider (database-direct
/// writers and SFTP aren't covered yet); callers must treat a missing provider as "not checked", never fake a result.
/// </summary>
public interface IDestinationHealthCheckProvider
{
    DestinationType DestinationType { get; }

    Task<DestinationHealthCheckResult> CheckAsync(DestinationConfiguration destination, CancellationToken cancellationToken);
}

public sealed record DestinationHealthCheckResult(bool IsSuccessful, string? Message);

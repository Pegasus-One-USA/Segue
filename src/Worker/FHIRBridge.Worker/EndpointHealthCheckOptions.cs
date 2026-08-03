namespace FHIRBridge.Worker;

/// <summary>
/// Options for the endpoint health check worker role. Disabled by default; when enabled it periodically
/// tests every enabled source connection's connectivity via <c>ISourceConnectionTestService</c>, and every
/// enabled destination with a registered <see cref="FHIRBridge.Application.Abstractions.Destinations.IDestinationHealthCheckProvider"/>
/// for its type (see <c>TargetReachabilityDestinationHealthCheckProvider</c>'s remarks for exactly which
/// destination types that covers) — recording the result for each. Destination types with no registered
/// provider (SQL-direct writers, SFTP) are skipped, not faked.
/// </summary>
public sealed class EndpointHealthCheckOptions
{
    public bool Enabled { get; set; }

    public int IntervalSeconds { get; set; } = 300;
}

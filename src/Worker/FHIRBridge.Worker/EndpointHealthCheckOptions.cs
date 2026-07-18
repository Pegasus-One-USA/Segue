namespace FHIRBridge.Worker;

/// <summary>
/// Options for the endpoint health check worker role. Disabled by default; when enabled it periodically
/// tests every enabled source connection's connectivity via <c>ISourceConnectionTestService</c> and records
/// the result. Destination connectivity isn't covered — there's no generic per-destination-type test
/// service today (only source connections have one), just per-type "test before save" flows used by the
/// configuration wizard.
/// </summary>
public sealed class EndpointHealthCheckOptions
{
    public bool Enabled { get; set; }

    public int IntervalSeconds { get; set; } = 300;
}

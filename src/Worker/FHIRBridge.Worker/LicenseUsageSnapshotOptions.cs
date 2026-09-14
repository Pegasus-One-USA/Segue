namespace FHIRBridge.Worker;

/// <summary>
/// Options for the recurring, purely-local license usage snapshot worker role. Enabled by default: taking a
/// tamper-evident snapshot of live usage counts has zero network dependency, so there's no reason it should
/// ever be off by default the way <c>LicenseHeartbeat</c> (which does depend on network reachability) is.
/// </summary>
public sealed class LicenseUsageSnapshotOptions
{
    public bool Enabled { get; set; } = true;

    public int IntervalHours { get; set; } = 24;
}

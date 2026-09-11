namespace FHIRBridge.Worker;

/// <summary>
/// Options for the best-effort "phone home" heartbeat worker role. Unlike every other setting in this
/// options class, <see cref="EndpointUrl"/> deliberately has no default pointing at any real address — the
/// PegasusOne-hosted license check-in service this posts to didn't exist at a known address at the time
/// this was built. Leaving it unset is the supported, permanent configuration for an on-prem/air-gapped
/// install that never phones home at all: <c>LicenseHeartbeatWorker</c> no-ops (a Debug-level log line, no
/// network attempt) whenever it's blank, exactly like a real endpoint that's merely unreachable — see that
/// worker's remarks for why this must never be louder than a no-op.
/// </summary>
public sealed class LicenseHeartbeatOptions
{
    public bool Enabled { get; set; } = true;

    public int IntervalMinutes { get; set; } = 60;

    /// <summary>Base URL of the remote license check-in service (e.g. "https://license.pegasusone.com").
    /// <c>/api/checkin</c> is appended automatically unless the configured value already ends with it.
    /// Empty/unset by default — see this class's remarks.</summary>
    public string? EndpointUrl { get; set; }

    public int TimeoutSeconds { get; set; } = 15;
}

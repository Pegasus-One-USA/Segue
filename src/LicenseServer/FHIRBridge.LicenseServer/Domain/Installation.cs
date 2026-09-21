namespace FHIRBridge.LicenseServer.Domain;

/// <summary>
/// One deployed FHIRBridge instance that has checked in at least once. Upserted on every
/// <c>POST /api/checkin</c> — always reflects the MOST RECENT check-in only; see <see cref="CheckIn"/> for
/// the full history.
/// </summary>
public sealed class Installation
{
    /// <summary>Stable id the deployed instance generates for itself and sends on every check-in.</summary>
    public required string InstallationId { get; set; }

    public string? CustomerId { get; set; }

    public string? CustomerName { get; set; }

    public string? Edition { get; set; }

    /// <summary>Best-effort link to the <see cref="IssuedLicense"/> this installation's most recent
    /// check-in's token exactly matched (see CheckInEndpoints). Null when no issued license in this
    /// server's own audit trail has that exact token — e.g. a license minted before this server existed,
    /// or via the main repo's own CLI minter tool. Lets the dashboard compute "used / limit" quota; when
    /// null it falls back to showing raw counts only.</summary>
    public Guid? CurrentIssuedLicenseId { get; set; }

    public IssuedLicense? CurrentIssuedLicense { get; set; }

    public DateTime LastSeenUtc { get; set; }

    public DateTime LastObservedUtc { get; set; }

    public int LastUserCount { get; set; }

    public int LastSourceConnectionCount { get; set; }

    public int LastTenantCount { get; set; }

    public int LastWorkflowCount { get; set; }

    public long LastCumulativeConfiguredPipelineRunCount { get; set; }

    public long LastCumulativeRuntimeWorkflowRunCount { get; set; }

    public long LastProcessedRecordsThisMonth { get; set; }
}

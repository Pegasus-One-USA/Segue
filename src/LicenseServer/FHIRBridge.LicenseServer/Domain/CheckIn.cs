namespace FHIRBridge.LicenseServer.Domain;

/// <summary>
/// One historical check-in row. <see cref="Installation"/> only ever holds the latest snapshot; every
/// check-in — successful signature verification required — also lands here so nothing is lost.
/// </summary>
public sealed class CheckIn
{
    public long Id { get; set; }

    public required string InstallationId { get; set; }

    /// <summary>Best-effort link to the <see cref="IssuedLicense"/> whose token exactly matched THIS
    /// specific historical check-in (see CheckInEndpoints). Same null-if-not-found tolerance as
    /// <see cref="Installation.CurrentIssuedLicenseId"/>.</summary>
    public Guid? CurrentIssuedLicenseId { get; set; }

    /// <summary>Timestamp the reporting instance itself claims to have observed its counts at (may lag
    /// behind <see cref="ReceivedAtUtc"/> if the instance was offline and is catching up).</summary>
    public DateTime ObservedUtc { get; set; }

    /// <summary>When this license server actually received and recorded the check-in.</summary>
    public DateTime ReceivedAtUtc { get; set; }

    public int UserCount { get; set; }

    public int SourceConnectionCount { get; set; }

    public int TenantCount { get; set; }

    public int WorkflowCount { get; set; }

    public long CumulativeConfiguredPipelineRunCount { get; set; }

    public long CumulativeRuntimeWorkflowRunCount { get; set; }

    public long ProcessedRecordsThisMonth { get; set; }
}

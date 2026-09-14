namespace FHIRBridge.Worker;

/// <summary>
/// Wire shape for the PegasusOne-hosted <c>FHIRBridge.LicenseServer</c> project's <c>POST /api/checkin</c>
/// endpoint — a separate, independently-built service, not part of this repo. Kept as a single pair of DTO
/// classes (rather than inline anonymous/JSON-string construction) specifically so that if that project's
/// own build finishes with slightly different field names, only this one file needs editing — nothing else
/// in <see cref="LicenseHeartbeatWorker"/> depends on the exact shape.
///
/// Serialized with <see cref="System.Text.Json.JsonNamingPolicy.CamelCase"/> (see
/// <see cref="LicenseHeartbeatWorker"/>'s <c>JsonOptions</c>) to match the documented
/// <c>installationId</c>/<c>licenseToken</c>/... casing.
/// </summary>
public sealed class LicenseCheckinRequest
{
    public string InstallationId { get; set; } = string.Empty;

    /// <summary>The raw signed JWS this install currently has applied (<see cref="FHIRBridge.Application.Abstractions.Licensing.ILicenseService.CurrentRawToken"/>),
    /// so the server can verify its signature itself. Null when no license is currently applied — the
    /// server-side contract for that case is up to the receiving service; this worker sends whatever
    /// <c>CurrentRawToken</c> currently is, unconditionally.</summary>
    public string? LicenseToken { get; set; }

    public DateTime ObservedUtc { get; set; }

    public LicenseCheckinCounts Counts { get; set; } = new();
}

public sealed class LicenseCheckinCounts
{
    public int UserCount { get; set; }

    public int SourceConnectionCount { get; set; }

    public int TenantCount { get; set; }

    public int WorkflowCount { get; set; }

    public long CumulativeConfiguredPipelineRunCount { get; set; }

    public long CumulativeRuntimeWorkflowRunCount { get; set; }

    public long ProcessedRecordsThisMonth { get; set; }
}

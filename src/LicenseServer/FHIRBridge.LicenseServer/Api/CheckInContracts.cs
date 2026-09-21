namespace FHIRBridge.LicenseServer.Api;

/// <summary>Request body for <c>POST /api/checkin</c>. New contract defined by this project — see
/// README.md for the full shape documented for client authors.</summary>
public sealed record CheckInRequest(
    string InstallationId,
    string LicenseToken,
    DateTime ObservedUtc,
    CheckInCounts Counts);

public sealed record CheckInCounts(
    int UserCount,
    int SourceConnectionCount,
    int TenantCount,
    int WorkflowCount,
    long CumulativeConfiguredPipelineRunCount,
    long CumulativeRuntimeWorkflowRunCount,
    long ProcessedRecordsThisMonth);

/// <summary>Ack body returned on a successful (200) check-in.</summary>
public sealed record CheckInResponse(
    string Status,
    string InstallationId,
    string? CustomerName,
    string? Edition,
    DateTime ReceivedAtUtc);

/// <summary>Error body returned on a rejected (401) check-in.</summary>
public sealed record CheckInError(string Error);

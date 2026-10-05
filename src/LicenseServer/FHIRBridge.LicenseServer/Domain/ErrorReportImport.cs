namespace FHIRBridge.LicenseServer.Domain;

/// <summary>
/// One error report file (exported from a client's Error Dashboard as JSON or CSV) that an admin imported
/// here so support can review that client's errors without any access to the client's environment.
/// </summary>
public sealed class ErrorReportImport
{
    public Guid Id { get; set; }

    /// <summary>Admin-chosen label for the import, shown in the list (e.g. "Acme - Oct 2026 outage").</summary>
    public required string Name { get; set; }

    /// <summary>Which client the report came from.</summary>
    public required string ClientName { get; set; }

    public DateTime ImportedAtUtc { get; set; }

    public required string ImportedBy { get; set; }

    public required string SourceFileName { get; set; }

    /// <summary>"json" or "csv".</summary>
    public required string Format { get; set; }

    /// <summary>Reporting period stated by the file (JSON) or derived from the earliest/latest row (CSV).</summary>
    public DateTime? ReportFromUtc { get; set; }

    public DateTime? ReportToUtc { get; set; }

    public DateTime? ReportGeneratedAtUtc { get; set; }

    public string? ApplicationVersion { get; set; }

    /// <summary>True if the client's export hit its row cap, i.e. older errors in the period were left out.</summary>
    public bool ReportTruncated { get; set; }

    public int ErrorCount { get; set; }

    public List<ErrorReportEntry> Entries { get; set; } = new();

    public List<ErrorReportCorrelation> Correlations { get; set; } = new();
}

/// <summary>One error row from an imported report. Stored as received (the client's export is already scrubbed).</summary>
public sealed class ErrorReportEntry
{
    public long Id { get; set; }

    public Guid ImportId { get; set; }

    public ErrorReportImport? Import { get; set; }

    public string? ErrorReferenceId { get; set; }

    public DateTime OccurredOnUtc { get; set; }

    public required string Severity { get; set; }

    public string? Category { get; set; }

    public string? Module { get; set; }

    public required string ExceptionType { get; set; }

    public required string Message { get; set; }

    public string? WhatToDo { get; set; }

    public string? Cause { get; set; }

    public required string Status { get; set; }

    public string? CorrelationId { get; set; }

    public string? ExecutionId { get; set; }

    public string? WorkflowId { get; set; }

    public string? WorkflowName { get; set; }

    public string? NodeName { get; set; }

    public string? NodeType { get; set; }

    public string? SourceName { get; set; }

    public string? DestinationName { get; set; }

    public string? ResourceType { get; set; }

    public string? EndpointId { get; set; }

    public string? TraceId { get; set; }

    public string? StackTrace { get; set; }
}

/// <summary>
/// The run timeline around one correlation id, as exported by the client (scheduler, API calls, retries, other
/// errors, exports…). Summary lines only - the client's export never includes audit payloads or user identities.
/// </summary>
public sealed class ErrorReportCorrelation
{
    public long Id { get; set; }

    public Guid ImportId { get; set; }

    public ErrorReportImport? Import { get; set; }

    public required string CorrelationId { get; set; }

    public string? RunStatus { get; set; }

    public DateTime? RunStartedUtc { get; set; }

    public DateTime? RunCompletedUtc { get; set; }

    public int? ExtractedCount { get; set; }

    public int? MappedCount { get; set; }

    public int? WrittenCount { get; set; }

    public bool Truncated { get; set; }

    /// <summary>JSON array of { occurredUtc, source, title, status, detail }.</summary>
    public required string EventsJson { get; set; }
}

namespace FHIRBridge.Runtime.Infrastructure.Connectors;

public sealed class FhirBulkExportOptions
{
    /// <summary>How long to wait between status polls when the server gives no Retry-After hint.</summary>
    public int DefaultPollIntervalSeconds { get; set; } = 5;

    /// <summary>Upper bound on a single poll wait, even if the server's Retry-After asks for longer.</summary>
    public int MaxPollIntervalSeconds { get; set; } = 60;

    /// <summary>Maximum number of status polls before giving up on the export job.</summary>
    public int MaxPollAttempts { get; set; } = 120;

    /// <summary>How many times to attempt each individual NDJSON file download before failing. A flaky FHIR edge
    /// (e.g. eCW) intermittently 401s or returns a gateway error for a single file even when the token is valid;
    /// retrying just that file (with a freshly acquired token) avoids restarting the whole multi-file download.</summary>
    public int MaxDownloadAttempts { get; set; } = 4;

    /// <summary>Base back-off between per-file download retries (grows linearly with the attempt number).</summary>
    public int DownloadRetryDelaySeconds { get; set; } = 3;
}

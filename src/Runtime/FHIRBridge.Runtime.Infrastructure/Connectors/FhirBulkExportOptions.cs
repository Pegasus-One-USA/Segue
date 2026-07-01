namespace FHIRBridge.Runtime.Infrastructure.Connectors;

public sealed class FhirBulkExportOptions
{
    /// <summary>How long to wait between status polls when the server gives no Retry-After hint.</summary>
    public int DefaultPollIntervalSeconds { get; set; } = 5;

    /// <summary>Upper bound on a single poll wait, even if the server's Retry-After asks for longer.</summary>
    public int MaxPollIntervalSeconds { get; set; } = 60;

    /// <summary>Maximum number of status polls before giving up on the export job.</summary>
    public int MaxPollAttempts { get; set; } = 120;
}

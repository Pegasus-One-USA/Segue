namespace FHIRBridge.Runtime.Infrastructure.Connectors;

public sealed class EpicFhirClientOptions
{
    public int MaxRetryCount { get; set; } = 3;
    public int BaseRetryDelayMilliseconds { get; set; } = 500;
    public int MaxRetryDelaySeconds { get; set; } = 30;
    public int MaxRetryJitterMilliseconds { get; set; } = 250;
    public int RequestTimeoutSeconds { get; set; } = 100;
    public int MinimumMillisecondsBetweenRequestsPerSource { get; set; } = 100;

    /// <summary>Wire-level diagnostics for outgoing FHIR calls (config section <c>Runtime:Epic:Diagnostics</c>).</summary>
    public FhirHttpDiagnosticsOptions Diagnostics { get; set; } = new();
}

/// <summary>
/// Logs every outgoing FHIR request and its response, with a trace of how the search was scoped, so two ways of
/// running the same workflow can be compared line by line. <b>TEMPORARY: ON by default while the eClinicalWorks 400 is
/// being diagnosed - set <see cref="Enabled"/>'s default (and appsettings) back to false afterwards.</b> When on, request query VALUES and
/// response bodies are written to the application log and so can contain PHI: use it only against sandbox / synthetic
/// data, and switch it off again afterwards. Secrets (Authorization, cookies, anything named key/token/secret) are
/// always masked.
/// </summary>
public sealed class FhirHttpDiagnosticsOptions
{
    // TEMP-DEBUG: true while diagnosing eCW. Revert to false (default) when done.
    public bool Enabled { get; set; } = true;

    /// <summary>False logs only parameter NAMES (name=&lt;withheld&gt;) instead of name=brown.</summary>
    public bool IncludeQueryValues { get; set; } = true;

    public bool IncludeResponseBody { get; set; } = true;

    /// <summary>Longest response body written to the log; the rest is cut off (the full length is still logged).</summary>
    public int MaxBodyCharacters { get; set; } = 8000;
}

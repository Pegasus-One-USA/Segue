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
/// TEMPORARY wire diagnostics for ONE outgoing FHIR request (see FhirSourceConnectorBase.DiagnosticTargetUrl): the full
/// request and response are written to the application log, so they can contain PHI. ON by default while the
/// eClinicalWorks 400 is diagnosed; set <see cref="Enabled"/>'s default back to false (or delete the feature) afterwards.
/// </summary>
public sealed class FhirHttpDiagnosticsOptions
{
    // TEMP-DEBUG: true while diagnosing eCW. Revert to false (default) when done.
    public bool Enabled { get; set; } = true;
}

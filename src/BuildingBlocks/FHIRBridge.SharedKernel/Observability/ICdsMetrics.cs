namespace FHIRBridge.SharedKernel.Observability;

/// <summary>
/// Records CDS Hooks calls for OpenTelemetry (meter "FHIRBridge.Cds"). Tags carry only the hook, service id and
/// outcome — never anything from the request body.
/// </summary>
public interface ICdsMetrics
{
    void RecordCall(CdsCallMetric metric);
}

/// <param name="Kind">"invoke" or "feedback".</param>
/// <param name="Hook">The hook name, or null when it was not known (e.g. an unknown service).</param>
/// <param name="ServiceId">The service id from the URL.</param>
/// <param name="Outcome">A CdsCallOutcome name (e.g. "Success", "Unauthorized").</param>
/// <param name="CardCount">Cards returned (invoke) or feedback items received (feedback).</param>
public sealed record CdsCallMetric(
    string Kind,
    string? Hook,
    string ServiceId,
    string Outcome,
    int HttpStatus,
    int CardCount,
    long DurationMs);

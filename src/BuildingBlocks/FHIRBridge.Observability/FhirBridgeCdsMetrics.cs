using System.Diagnostics;
using System.Diagnostics.Metrics;
using FHIRBridge.SharedKernel.Observability;

namespace FHIRBridge.Observability;

/// <summary>
/// OpenTelemetry meter and activity source for the public CDS Hooks endpoints: calls, cards returned, call
/// duration and errors, tagged by hook, service id and outcome. No in-process snapshot — the CDS Activity screen
/// reads the CdsCallLog table instead.
/// </summary>
public sealed class FhirBridgeCdsMetrics : ICdsMetrics, IDisposable
{
    public const string MeterName = "FHIRBridge.Cds";
    public const string ActivitySourceName = "FHIRBridge.Cds";

    /// <summary>
    /// Set on an outbound <see cref="System.Net.Http.HttpRequestMessage"/> whose URL holds PHI (the CDS FHIR
    /// callback: <c>Patient/{id}</c>, <c>?patient={id}</c>); the HttpClient trace instrumentation skips such
    /// requests so the URL never reaches a span.
    /// </summary>
    public static readonly System.Net.Http.HttpRequestOptionsKey<bool> UntracedRequest = new("FHIRBridge.Cds.UntracedRequest");

    /// <summary>Spans for CDS evaluations (handler run inside the request's ASP.NET Core activity).</summary>
    public static readonly ActivitySource ActivitySource = new(ActivitySourceName);

    private readonly Meter _meter;
    private readonly Counter<long> _calls;
    private readonly Counter<long> _cards;
    private readonly Counter<long> _errors;
    private readonly Histogram<double> _duration;

    public FhirBridgeCdsMetrics()
    {
        _meter = new Meter(MeterName);
        _calls = _meter.CreateCounter<long>(
            "fhirbridge.cds.calls", unit: "{call}", description: "CDS Hooks calls (invoke and feedback), tagged by outcome.");
        _cards = _meter.CreateCounter<long>(
            "fhirbridge.cds.cards", unit: "{card}", description: "Cards returned by CDS Hooks services.");
        _errors = _meter.CreateCounter<long>(
            "fhirbridge.cds.errors", unit: "{call}", description: "CDS Hooks calls that ended in a 5xx.");
        _duration = _meter.CreateHistogram<double>(
            "fhirbridge.cds.duration", unit: "ms", description: "CDS Hooks call duration.");
    }

    public void RecordCall(CdsCallMetric metric)
    {
        ArgumentNullException.ThrowIfNull(metric);

        var tags = new KeyValuePair<string, object?>[]
        {
            new("kind", metric.Kind),
            new("hook", metric.Hook ?? "unknown"),
            new("service_id", metric.ServiceId),
            new("outcome", metric.Outcome),
        };

        _calls.Add(1, tags);
        _duration.Record(metric.DurationMs, tags);
        if (metric.CardCount > 0 && metric.Kind == "invoke")
        {
            _cards.Add(metric.CardCount, tags);
        }

        if (metric.HttpStatus >= 500)
        {
            _errors.Add(1, tags);
        }
    }

    public void Dispose()
    {
        _meter.Dispose();
    }
}

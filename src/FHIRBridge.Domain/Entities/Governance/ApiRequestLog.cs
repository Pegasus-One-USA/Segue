using FHIRBridge.SharedKernel.Abstractions;

namespace FHIRBridge.Domain.Entities.Governance;

/// <summary>
/// Immutable record of one HTTP call, in either direction: an OUTBOUND call this system made
/// (source/destination/terminology clients) or an INBOUND call a caller made to this system's own API — see
/// <see cref="Direction"/>. Query string is deliberately never stored — some upstream APIs put tokens there, and
/// search params can carry PHI-ish identifiers.
/// <para>Request/response headers and bodies are normally never captured either — see the four properties below.
/// They are populated ONLY for the temporary API Endpoint destination troubleshooting capture (see
/// <see cref="Infrastructure.Governance.ApiRequestLoggingHandler"/>/<see cref="Infrastructure.Destinations.ApiEndpoint.ApiEndpointSender"/>),
/// gated by the same Debug log level as its file-log counterpart. Every other outbound call, and every inbound
/// call, leaves these four columns null exactly as before. THIS IS TEMPORARY, LOCAL-DEBUG-ONLY CAPTURE: a
/// captured body can contain patient-identifying fields (name, birth date, identifiers) — remove the capture
/// call sites (and, ideally, this column set) once the troubleshooting that motivated it is done.</para>
/// <para>Inbound capture exists because a request this API refuses (an unknown workflow, a workflow not opted
/// into public launch) makes no outbound call, throws no exception, and starts no run — so without a row here
/// there is no durable record anywhere that the caller was refused, only a Serilog line.</para>
/// </summary>
public sealed class ApiRequestLog : Entity<Guid>, IAppendOnlyEntity
{
    private ApiRequestLog()
    {
    }

    public ApiRequestLog(
        Guid id,
        DateTime occurredOnUtc,
        string method,
        string url,
        int? statusCode,
        long durationMs,
        string? error,
        string? correlationId,
        string direction = ApiRequestDirection.Outbound,
        string? requestHeaders = null,
        string? requestBody = null,
        string? responseHeaders = null,
        string? responseBody = null)
    {
        Id = id;
        OccurredOnUtc = occurredOnUtc;
        Method = method;
        Url = url;
        StatusCode = statusCode;
        DurationMs = durationMs;
        Error = error;
        CorrelationId = correlationId;
        Direction = direction;
        RequestHeaders = requestHeaders;
        RequestBody = requestBody;
        ResponseHeaders = responseHeaders;
        ResponseBody = responseBody;
    }

    public DateTime OccurredOnUtc { get; private set; }
    public string Method { get; private set; } = default!;
    public string Url { get; private set; } = default!;
    public int? StatusCode { get; private set; }
    public long DurationMs { get; private set; }
    public string? Error { get; private set; }
    public string? CorrelationId { get; private set; }

    /// <summary>Temporary troubleshooting capture only — see the class doc comment.</summary>
    public string? RequestHeaders { get; private set; }

    /// <summary>Temporary troubleshooting capture only — see the class doc comment.</summary>
    public string? RequestBody { get; private set; }

    /// <summary>Temporary troubleshooting capture only — see the class doc comment.</summary>
    public string? ResponseHeaders { get; private set; }

    /// <summary>Temporary troubleshooting capture only — see the class doc comment.</summary>
    public string? ResponseBody { get; private set; }

    /// <summary>One of <see cref="ApiRequestDirection"/>. Defaulted rather than required so existing rows
    /// (all of which predate inbound capture) keep their original meaning without a data backfill.</summary>
    public string Direction { get; private set; } = ApiRequestDirection.Outbound;
}

/// <summary>The two values <see cref="ApiRequestLog.Direction"/> takes.</summary>
public static class ApiRequestDirection
{
    /// <summary>A call this system made to an external system (EHR, destination, terminology server).</summary>
    public const string Outbound = "Outbound";

    /// <summary>A call a caller made to this system's own API.</summary>
    public const string Inbound = "Inbound";
}

using FHIRBridge.SharedKernel.Abstractions;

namespace FHIRBridge.Domain.Entities.Governance;

/// <summary>
/// Immutable record of one outbound HTTP call (source/destination/terminology clients). Query string is
/// deliberately never stored — some upstream APIs put tokens there, and search params can carry PHI-ish
/// identifiers. Never captures request/response bodies or headers.
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
        string? correlationId)
    {
        Id = id;
        OccurredOnUtc = occurredOnUtc;
        Method = method;
        Url = url;
        StatusCode = statusCode;
        DurationMs = durationMs;
        Error = error;
        CorrelationId = correlationId;
    }

    public DateTime OccurredOnUtc { get; private set; }
    public string Method { get; private set; } = default!;
    public string Url { get; private set; } = default!;
    public int? StatusCode { get; private set; }
    public long DurationMs { get; private set; }
    public string? Error { get; private set; }
    public string? CorrelationId { get; private set; }
}

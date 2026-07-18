using FHIRBridge.SharedKernel.Abstractions;

namespace FHIRBridge.Domain.Entities.Governance;

/// <summary>Immutable record of an unhandled exception, captured centrally (Api global handler, Worker hosts).</summary>
public sealed class ErrorLog : Entity<Guid>, IAppendOnlyEntity
{
    private ErrorLog()
    {
    }

    public ErrorLog(
        Guid id,
        DateTime occurredOnUtc,
        string severity,
        string exceptionType,
        string message,
        string? stackTrace,
        string? module,
        string? correlationId)
    {
        Id = id;
        OccurredOnUtc = occurredOnUtc;
        Severity = severity;
        ExceptionType = exceptionType;
        Message = message;
        StackTrace = stackTrace;
        Module = module;
        CorrelationId = correlationId;
    }

    public DateTime OccurredOnUtc { get; private set; }
    public string Severity { get; private set; } = default!;
    public string ExceptionType { get; private set; } = default!;
    public string Message { get; private set; } = default!;
    public string? StackTrace { get; private set; }
    public string? Module { get; private set; }
    public string? CorrelationId { get; private set; }
}

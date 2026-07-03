using FHIRBridge.Application.Abstractions.Audit;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Runtime.Application.Abstractions.Auth;
using FHIRBridge.Runtime.Application.DTOs;

namespace FHIRBridge.Infrastructure.Audit;

public sealed class FhirAccessTokenAuditSink : IFhirAccessTokenAuditSink
{
    private readonly IOperationalAuditService _auditService;

    public FhirAccessTokenAuditSink(IOperationalAuditService auditService)
    {
        _auditService = auditService;
    }

    public Task RecordAsync(
        FhirSourceConfiguration source,
        string action,
        string status,
        string message,
        CancellationToken cancellationToken)
    {
        return _auditService.RecordAsync(
            new RecordOperationalAuditLogRequest(
                null,
                null,
                source.SourceConnectionId,
                null,
                null,
                null,
                action,
                status,
                message,
                null,
                "system",
                null),
            cancellationToken);
    }
}

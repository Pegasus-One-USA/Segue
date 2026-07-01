using FHIRBridge.Runtime.Application.DTOs;

namespace FHIRBridge.Runtime.Application.Abstractions.Auth;

public interface IFhirAccessTokenAuditSink
{
    Task RecordAsync(
        FhirSourceConfiguration source,
        string action,
        string status,
        string message,
        CancellationToken cancellationToken);
}

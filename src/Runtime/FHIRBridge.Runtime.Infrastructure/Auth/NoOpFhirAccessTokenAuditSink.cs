using FHIRBridge.Runtime.Application.Abstractions.Auth;
using FHIRBridge.Runtime.Application.DTOs;

namespace FHIRBridge.Runtime.Infrastructure.Auth;

public sealed class NoOpFhirAccessTokenAuditSink : IFhirAccessTokenAuditSink
{
    public Task RecordAsync(
        FhirSourceConfiguration source,
        string action,
        string status,
        string message,
        CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}

namespace FHIRBridge.Application.DTOs;

public sealed record SourceConnectionTestResultDto(
    Guid TenantId,
    Guid SourceConnectionId,
    string SourceSystemType,
    bool IsSuccessful,
    string Status,
    string Message,
    DateTime TestedOnUtc);

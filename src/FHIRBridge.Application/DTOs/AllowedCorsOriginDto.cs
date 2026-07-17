namespace FHIRBridge.Application.DTOs;

public sealed record AllowedCorsOriginDto(
    Guid Id,
    string OriginUrl,
    string? Label,
    DateTime CreatedOnUtc,
    string? CreatedBy);

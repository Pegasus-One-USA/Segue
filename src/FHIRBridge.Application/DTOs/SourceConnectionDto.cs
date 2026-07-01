using FHIRBridge.Domain.Enums;

namespace FHIRBridge.Application.DTOs;

public sealed record SourceConnectionDto(
    Guid Id,
    string Name,
    SourceSystemType SourceSystemType,
    string BaseUrl,
    SourceAuthenticationDto Authentication,
    bool IsEnabled);

using FHIRBridge.Domain.Enums;

namespace FHIRBridge.Application.DTOs;

public sealed record CreateSourceConnectionRequest(
    string Name,
    SourceSystemType SourceSystemType,
    string BaseUrl,
    SourceAuthenticationDto Authentication);

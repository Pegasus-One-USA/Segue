namespace FHIRBridge.Application.DTOs;

public sealed record CreateAllowedCorsOriginRequest(string OriginUrl, string? Label);

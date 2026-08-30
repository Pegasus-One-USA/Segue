namespace FHIRBridge.Application.DTOs;

public sealed record UpdateAllowedCorsOriginRequest(string OriginUrl, string? Label);

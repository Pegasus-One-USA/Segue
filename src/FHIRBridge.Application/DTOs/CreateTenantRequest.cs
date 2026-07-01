namespace FHIRBridge.Application.DTOs;

public sealed record CreateTenantRequest(
    string Name,
    string Code);

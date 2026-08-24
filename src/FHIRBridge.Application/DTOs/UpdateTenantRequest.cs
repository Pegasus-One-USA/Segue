namespace FHIRBridge.Application.DTOs;

public sealed record UpdateTenantRequest(string Name, string Code, bool IsActive);

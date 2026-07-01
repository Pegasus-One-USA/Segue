namespace FHIRBridge.Application.DTOs;

public sealed record RegisterTenantRequest(
    string OrgName,
    string OrgType,
    string Country,
    string Timezone,
    string AdminEmail,
    string AdminPassword,
    string? AdminFirstName,
    string? AdminLastName);

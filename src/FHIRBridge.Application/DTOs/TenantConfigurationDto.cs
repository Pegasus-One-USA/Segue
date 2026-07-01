using FHIRBridge.Domain.Enums;

namespace FHIRBridge.Application.DTOs;

public sealed record TenantConfigurationDto(
    Guid Id,
    string Name,
    string Code,
    TenantStatus Status);

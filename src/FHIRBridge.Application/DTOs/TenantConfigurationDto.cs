using FHIRBridge.Domain.Enums;

namespace FHIRBridge.Application.DTOs;

public sealed record TenantConfigurationDto(
    Guid Id,
    string Name,
    string Code,
    TenantStatus Status,
    IReadOnlyList<SourceConnectionDto> SourceConnections,
    IReadOnlyList<WebhookConfigurationDto> WebhookConfigurations,
    IReadOnlyList<DestinationConfigurationDto> DestinationConfigurations,
    IReadOnlyList<MappingProfileDto> MappingProfiles,
    IReadOnlyList<ResourceConfigurationDto> ResourceConfigurations);

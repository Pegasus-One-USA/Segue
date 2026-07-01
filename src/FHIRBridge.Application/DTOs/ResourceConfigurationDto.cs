using FHIRBridge.Domain.Enums;

namespace FHIRBridge.Application.DTOs;

public sealed record ResourceConfigurationDto(
    Guid Id,
    string ResourceType,
    bool IsEnabled,
    IReadOnlyList<ResourcePipelineRouteDto> Routes);

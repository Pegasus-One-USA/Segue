using FHIRBridge.Domain.Enums;

namespace FHIRBridge.Application.DTOs;

public sealed record ResourcePipelineRouteDto(
    Guid Id,
    Guid? WebhookConfigurationId,
    Guid MappingProfileId,
    IngestionMode IngestionMode,
    string? ScheduleExpression,
    string? SearchParameters,
    bool IsEnabled,
    int Priority);

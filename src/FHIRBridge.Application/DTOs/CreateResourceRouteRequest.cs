using FHIRBridge.Domain.Enums;

namespace FHIRBridge.Application.DTOs;

public sealed record CreateResourceRouteRequest(
    IngestionMode IngestionMode,
    Guid? WebhookConfigurationId,
    Guid MappingProfileId,
    string? ScheduleExpression,
    string? SearchParameters,
    bool IsEnabled,
    int Priority);

using FHIRBridge.Domain.Enums;

namespace FHIRBridge.Application.DTOs;

public sealed record ConfigureResourceRequest(
    bool IsEnabled,
    IngestionMode IngestionMode,
    Guid? WebhookConfigurationId,
    Guid MappingProfileId,
    string? ScheduleExpression,
    string? SearchParameters);

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
    int Priority,
    IReadOnlyList<ResourcePipelineRouteMappingDto> ResourceMappings,
    string TimeZoneId = "UTC");

public sealed record ResourcePipelineRouteMappingDto(
    Guid Id,
    Guid MappingProfileId,
    bool IsEnabled,
    int ExecutionOrder,
    string? SearchParameters,
    IReadOnlyList<ParentReferenceDto> ParentReferences);

public sealed record ParentReferenceDto(
    Guid ParentMappingProfileId,
    string? ReferenceFieldOverride);

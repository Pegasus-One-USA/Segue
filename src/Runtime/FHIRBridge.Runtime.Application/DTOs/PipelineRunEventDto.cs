using FHIRBridge.Runtime.Domain.Enums;

namespace FHIRBridge.Runtime.Application.DTOs;

public sealed record PipelineRunEventDto(
    Guid Id,
    Guid PipelineRunId,
    string EventType,
    PipelineStepType? StepType,
    string? ResourceType,
    string? ResourceId,
    string Message,
    string? CorrelationId,
    DateTime OccurredOnUtc);

using FHIRBridge.Runtime.Domain.Enums;

namespace FHIRBridge.Runtime.Application.DTOs;

public sealed record PipelineStepDto(
    Guid Id,
    PipelineStepType StepType,
    PipelineStepStatus Status,
    string? ResourceType,
    int ResourceCount,
    string? Message,
    DateTime StartedOnUtc,
    DateTime? CompletedOnUtc);

using FHIRBridge.Runtime.Domain.Enums;

namespace FHIRBridge.Runtime.Application.DTOs;

public sealed record PipelineRunDto(
    Guid Id,
    RuntimeSourceType SourceType,
    RuntimeDestinationType DestinationType,
    PipelineRunStatus Status,
    IReadOnlyList<string> RequestedResourceTypes,
    int ExtractedResourceCount,
    int WrittenResourceCount,
    string? FailureMessage,
    DateTime StartedOnUtc,
    DateTime? CompletedOnUtc,
    IReadOnlyList<PipelineStepDto> Steps,
    string? ErrorReferenceId = null);

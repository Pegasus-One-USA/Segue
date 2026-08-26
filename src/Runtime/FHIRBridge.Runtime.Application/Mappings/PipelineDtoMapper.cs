using FHIRBridge.Runtime.Application.DTOs;
using FHIRBridge.Runtime.Domain.Entities;

namespace FHIRBridge.Runtime.Application.Mappings;

public static class PipelineDtoMapper
{
    public static PipelineRunDto ToDto(PipelineRun pipelineRun)
    {
        return new PipelineRunDto(
            pipelineRun.Id,
            pipelineRun.SourceType,
            pipelineRun.DestinationType,
            pipelineRun.Status,
            pipelineRun.RequestedResourceTypes,
            pipelineRun.ExtractedResourceCount,
            pipelineRun.WrittenResourceCount,
            pipelineRun.FailureMessage,
            pipelineRun.StartedOnUtc,
            pipelineRun.CompletedOnUtc,
            pipelineRun.Steps
                .Select(step => new PipelineStepDto(
                    step.Id,
                    step.StepType,
                    step.Status,
                    step.ResourceType,
                    step.ResourceCount,
                    step.Message,
                    step.StartedOnUtc,
                    step.CompletedOnUtc))
                .ToList(),
            pipelineRun.ErrorReferenceId);
    }

    public static PipelineRunEventDto ToDto(PipelineRunEvent pipelineRunEvent)
    {
        return new PipelineRunEventDto(
            pipelineRunEvent.Id,
            pipelineRunEvent.PipelineRunId,
            pipelineRunEvent.EventType,
            pipelineRunEvent.StepType,
            pipelineRunEvent.ResourceType,
            pipelineRunEvent.ResourceId,
            pipelineRunEvent.Message,
            pipelineRunEvent.CorrelationId,
            pipelineRunEvent.OccurredOnUtc);
    }
}

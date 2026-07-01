using FHIRBridge.Runtime.Application.Abstractions.Persistence;
using FHIRBridge.Runtime.Application.DTOs;
using FHIRBridge.Runtime.Application.Mappings;
using MediatR;

namespace FHIRBridge.Runtime.Application.Pipeline.Queries;

public sealed class GetPipelineRunEventsQueryHandler
    : IRequestHandler<GetPipelineRunEventsQuery, IReadOnlyList<PipelineRunEventDto>>
{
    private readonly IPipelineRunStore _pipelineRunStore;

    public GetPipelineRunEventsQueryHandler(IPipelineRunStore pipelineRunStore)
    {
        _pipelineRunStore = pipelineRunStore;
    }

    public async Task<IReadOnlyList<PipelineRunEventDto>> Handle(
        GetPipelineRunEventsQuery request,
        CancellationToken cancellationToken)
    {
        var events = await _pipelineRunStore.GetEventsAsync(request.PipelineRunId, cancellationToken);

        return events
            .Select(PipelineDtoMapper.ToDto)
            .ToList();
    }
}

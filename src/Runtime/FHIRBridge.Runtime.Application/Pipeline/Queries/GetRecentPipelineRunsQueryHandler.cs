using FHIRBridge.Runtime.Application.Abstractions.Persistence;
using FHIRBridge.Runtime.Application.DTOs;
using FHIRBridge.Runtime.Application.Mappings;
using MediatR;

namespace FHIRBridge.Runtime.Application.Pipeline.Queries;

public sealed class GetRecentPipelineRunsQueryHandler
    : IRequestHandler<GetRecentPipelineRunsQuery, IReadOnlyList<PipelineRunDto>>
{
    private readonly IPipelineRunStore _pipelineRunStore;

    public GetRecentPipelineRunsQueryHandler(IPipelineRunStore pipelineRunStore)
    {
        _pipelineRunStore = pipelineRunStore;
    }

    public async Task<IReadOnlyList<PipelineRunDto>> Handle(
        GetRecentPipelineRunsQuery request,
        CancellationToken cancellationToken)
    {
        var count = request.Count <= 0 ? 20 : Math.Min(request.Count, 100);
        var pipelineRuns = await _pipelineRunStore.GetRecentAsync(count, cancellationToken);

        return pipelineRuns
            .Select(PipelineDtoMapper.ToDto)
            .ToList();
    }
}

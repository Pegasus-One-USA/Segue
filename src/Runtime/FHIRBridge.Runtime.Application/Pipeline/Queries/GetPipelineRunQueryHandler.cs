using FHIRBridge.Runtime.Application.Abstractions.Persistence;
using FHIRBridge.Runtime.Application.DTOs;
using FHIRBridge.Runtime.Application.Mappings;
using MediatR;

namespace FHIRBridge.Runtime.Application.Pipeline.Queries;

public sealed class GetPipelineRunQueryHandler : IRequestHandler<GetPipelineRunQuery, PipelineRunDto?>
{
    private readonly IPipelineRunStore _pipelineRunStore;

    public GetPipelineRunQueryHandler(IPipelineRunStore pipelineRunStore)
    {
        _pipelineRunStore = pipelineRunStore;
    }

    public async Task<PipelineRunDto?> Handle(
        GetPipelineRunQuery request,
        CancellationToken cancellationToken)
    {
        var pipelineRun = await _pipelineRunStore.GetAsync(request.PipelineRunId, cancellationToken);

        return pipelineRun is null ? null : PipelineDtoMapper.ToDto(pipelineRun);
    }
}

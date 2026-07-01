using FHIRBridge.Runtime.Application.Abstractions.Pipeline;
using FHIRBridge.Runtime.Application.DTOs;
using MediatR;

namespace FHIRBridge.Runtime.Application.Pipeline.Commands;

public sealed class StartPipelineRunCommandHandler : IRequestHandler<StartPipelineRunCommand, PipelineRunDto>
{
    private readonly IPipelineOrchestrator _pipelineOrchestrator;

    public StartPipelineRunCommandHandler(IPipelineOrchestrator pipelineOrchestrator)
    {
        _pipelineOrchestrator = pipelineOrchestrator;
    }

    public Task<PipelineRunDto> Handle(
        StartPipelineRunCommand request,
        CancellationToken cancellationToken)
    {
        return _pipelineOrchestrator.StartAsync(request.Request, cancellationToken);
    }
}

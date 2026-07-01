using FHIRBridge.Runtime.Application.Abstractions.Pipeline;
using FHIRBridge.Runtime.Application.DTOs;
using MediatR;

namespace FHIRBridge.Runtime.Application.Pipeline.Commands;

public sealed class StartBulkExportRunCommandHandler : IRequestHandler<StartBulkExportRunCommand, PipelineRunDto>
{
    private readonly IPipelineOrchestrator _pipelineOrchestrator;

    public StartBulkExportRunCommandHandler(IPipelineOrchestrator pipelineOrchestrator)
    {
        _pipelineOrchestrator = pipelineOrchestrator;
    }

    public Task<PipelineRunDto> Handle(
        StartBulkExportRunCommand request,
        CancellationToken cancellationToken)
    {
        return _pipelineOrchestrator.StartBulkExportAsync(request.Request, cancellationToken);
    }
}

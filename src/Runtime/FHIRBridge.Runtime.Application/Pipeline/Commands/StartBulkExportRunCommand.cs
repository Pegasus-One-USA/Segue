using FHIRBridge.Runtime.Application.DTOs;
using MediatR;

namespace FHIRBridge.Runtime.Application.Pipeline.Commands;

public sealed record StartBulkExportRunCommand(StartBulkExportRunRequest Request) : IRequest<PipelineRunDto>;

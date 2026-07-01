using FHIRBridge.Runtime.Application.DTOs;
using MediatR;

namespace FHIRBridge.Runtime.Application.Pipeline.Commands;

public sealed record StartPipelineRunCommand(StartPipelineRunRequest Request) : IRequest<PipelineRunDto>;

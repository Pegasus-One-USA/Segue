using FHIRBridge.Runtime.Application.DTOs;
using MediatR;

namespace FHIRBridge.Runtime.Application.Pipeline.Queries;

public sealed record GetRecentPipelineRunsQuery(int Count) : IRequest<IReadOnlyList<PipelineRunDto>>;

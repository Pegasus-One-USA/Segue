using FHIRBridge.Application.DTOs;

namespace FHIRBridge.Application.Abstractions.Sources;

public interface ISourceConnectionTestService
{
    Task<SourceConnectionTestResultDto> TestAsync(
        Guid sourceConnectionId,
        CancellationToken cancellationToken);
}

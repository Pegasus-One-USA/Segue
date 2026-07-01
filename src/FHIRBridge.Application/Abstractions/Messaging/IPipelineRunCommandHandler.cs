using FHIRBridge.Application.Messaging;

namespace FHIRBridge.Application.Abstractions.Messaging;

/// <summary>
/// Executes a <see cref="PipelineRunCommand"/> consumed from the messaging transport: enforces idempotency, then
/// runs the configured pipeline. Kept separate from the hosting background service so it is unit-testable.
/// </summary>
public interface IPipelineRunCommandHandler
{
    Task HandleAsync(PipelineRunCommand command, CancellationToken cancellationToken);
}

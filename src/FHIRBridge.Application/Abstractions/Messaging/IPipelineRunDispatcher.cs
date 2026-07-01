using FHIRBridge.Application.Messaging;

namespace FHIRBridge.Application.Abstractions.Messaging;

/// <summary>Publishes a <see cref="PipelineRunCommand"/> to the messaging transport.</summary>
public interface IPipelineRunDispatcher
{
    Task EnqueueAsync(PipelineRunCommand command, CancellationToken cancellationToken);
}

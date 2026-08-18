using FHIRBridge.Application.Messaging;

namespace FHIRBridge.Application.Abstractions.Messaging;

/// <summary>Publishes a <see cref="LineageCaptureCommand"/> to the messaging transport.</summary>
public interface ILineageCaptureDispatcher
{
    Task EnqueueAsync(LineageCaptureCommand command, CancellationToken cancellationToken);
}

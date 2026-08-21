using FHIRBridge.Application.Messaging;

namespace FHIRBridge.Application.Abstractions.Messaging;

/// <summary>
/// Executes a <see cref="LineageCaptureCommand"/> consumed from the messaging transport: enforces idempotency,
/// then bulk-inserts the field-lineage hops. Kept separate from the hosting background service so it is
/// unit-testable.
/// </summary>
public interface ILineageCaptureCommandHandler
{
    Task HandleAsync(LineageCaptureCommand command, CancellationToken cancellationToken);
}

using FHIRBridge.Application.Abstractions.Messaging;
using FHIRBridge.Application.Messaging;

namespace FHIRBridge.Infrastructure.Messaging;

public sealed class InMemoryLineageCaptureDispatcher : ILineageCaptureDispatcher
{
    private readonly InMemoryMessageChannel<LineageCaptureCommand> _channel;

    public InMemoryLineageCaptureDispatcher(InMemoryMessageChannel<LineageCaptureCommand> channel)
    {
        _channel = channel;
    }

    public async Task EnqueueAsync(LineageCaptureCommand command, CancellationToken cancellationToken)
    {
        await _channel.Writer.WriteAsync(command, cancellationToken);
    }
}

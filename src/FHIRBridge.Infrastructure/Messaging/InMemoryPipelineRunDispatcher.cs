using FHIRBridge.Application.Abstractions.Messaging;
using FHIRBridge.Application.Messaging;

namespace FHIRBridge.Infrastructure.Messaging;

public sealed class InMemoryPipelineRunDispatcher : IPipelineRunDispatcher
{
    private readonly InMemoryMessageChannel<PipelineRunCommand> _channel;

    public InMemoryPipelineRunDispatcher(InMemoryMessageChannel<PipelineRunCommand> channel)
    {
        _channel = channel;
    }

    public async Task EnqueueAsync(PipelineRunCommand command, CancellationToken cancellationToken)
    {
        await _channel.Writer.WriteAsync(command, cancellationToken);
    }
}

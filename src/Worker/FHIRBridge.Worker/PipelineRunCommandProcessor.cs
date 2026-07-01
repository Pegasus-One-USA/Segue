using FHIRBridge.Application.Abstractions.Messaging;
using FHIRBridge.Application.Messaging;

namespace FHIRBridge.Worker;

/// <summary>
/// Processor worker role: consumes <see cref="PipelineRunCommand"/> messages from the transport and runs each via
/// the scoped <see cref="IPipelineRunCommandHandler"/>. Always on — it idles until commands arrive. In Azure this
/// role maps to an event-triggered Container Apps Job scaled by queue depth (Phase 4).
/// </summary>
public sealed class PipelineRunCommandProcessor : BackgroundService
{
    private readonly IMessageConsumer<PipelineRunCommand> _consumer;
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly ILogger<PipelineRunCommandProcessor> _logger;

    public PipelineRunCommandProcessor(
        IMessageConsumer<PipelineRunCommand> consumer,
        IServiceScopeFactory serviceScopeFactory,
        ILogger<PipelineRunCommandProcessor> logger)
    {
        _consumer = consumer;
        _serviceScopeFactory = serviceScopeFactory;
        _logger = logger;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Pipeline run command processor started; waiting for scheduled run commands.");
        return _consumer.StartAsync(HandleAsync, stoppingToken);
    }

    private async Task HandleAsync(PipelineRunCommand command, CancellationToken cancellationToken)
    {
        using var scope = _serviceScopeFactory.CreateScope();
        var handler = scope.ServiceProvider.GetRequiredService<IPipelineRunCommandHandler>();
        await handler.HandleAsync(command, cancellationToken);
    }
}

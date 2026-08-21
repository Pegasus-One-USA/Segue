using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FHIRBridge.Infrastructure.Terminology;

/// <summary>Drains <see cref="TerminologyImportChannel"/> one job at a time, each in its own DI scope (a
/// fresh <c>FHIRBridgeDbContext</c> per job, since the enqueuing HTTP request's scope is long gone by the
/// time this runs). Individual import services record their own failures into their *ImportHistory table;
/// the catch here is only a last-resort net for failures before that point (e.g. DI resolution itself).</summary>
public sealed class TerminologyImportBackgroundService : BackgroundService
{
    private readonly TerminologyImportChannel _channel;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<TerminologyImportBackgroundService> _logger;

    public TerminologyImportBackgroundService(TerminologyImportChannel channel, IServiceScopeFactory scopeFactory, ILogger<TerminologyImportBackgroundService> logger)
    {
        _channel = channel;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var job in _channel.Reader.ReadAllAsync(stoppingToken))
        {
            using var scope = _scopeFactory.CreateScope();
            try
            {
                await job(scope.ServiceProvider, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "A terminology import background job failed.");
            }
        }
    }
}

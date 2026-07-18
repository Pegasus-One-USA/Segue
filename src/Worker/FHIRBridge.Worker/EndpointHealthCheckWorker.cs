using System.Diagnostics;
using FHIRBridge.Application.Abstractions.Destinations;
using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.Abstractions.Sources;
using FHIRBridge.Governance;
using Microsoft.Extensions.Options;

namespace FHIRBridge.Worker;

/// <summary>
/// Endpoint health worker role: on a timer, tests every enabled source connection's connectivity via the
/// existing <see cref="ISourceConnectionTestService"/> (the same service the configuration wizard's "Test
/// Connection" button uses), and every enabled destination that has a registered
/// <see cref="IDestinationHealthCheckProvider"/> for its type — destinations without one (database-direct
/// writers, SFTP) are skipped, not faked. Disabled by default.
/// </summary>
public sealed class EndpointHealthCheckWorker : BackgroundService
{
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly IOptions<EndpointHealthCheckOptions> _options;
    private readonly ILogger<EndpointHealthCheckWorker> _logger;

    public EndpointHealthCheckWorker(
        IServiceScopeFactory serviceScopeFactory,
        IOptions<EndpointHealthCheckOptions> options,
        ILogger<EndpointHealthCheckWorker> logger)
    {
        _serviceScopeFactory = serviceScopeFactory;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Value.Enabled)
        {
            _logger.LogInformation(
                "Endpoint health check worker is disabled. Set EndpointHealthCheck:Enabled=true to periodically test source connectivity.");
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(60, _options.Value.IntervalSeconds)));

        do
        {
            await CheckAllAsync(stoppingToken);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task CheckAllAsync(CancellationToken cancellationToken)
    {
        using var scope = _serviceScopeFactory.CreateScope();
        var configurationRepository = scope.ServiceProvider.GetRequiredService<IConfigurationRepository>();
        var testService = scope.ServiceProvider.GetRequiredService<ISourceConnectionTestService>();
        var governanceLogger = scope.ServiceProvider.GetRequiredService<IGovernanceLogger>();

        var sourceConnections = await configurationRepository.GetSourceConnectionsAsync(cancellationToken);

        foreach (var sourceConnection in sourceConnections.Where(s => s.IsEnabled))
        {
            var stopwatch = Stopwatch.StartNew();

            try
            {
                var result = await testService.TestAsync(sourceConnection.Id, cancellationToken);
                stopwatch.Stop();

                await governanceLogger.LogEndpointHealthAsync(
                    new EndpointHealthEntry(
                        sourceConnection.Name,
                        "Source",
                        result.IsSuccessful ? "Healthy" : "Offline",
                        stopwatch.ElapsedMilliseconds,
                        result.Message),
                    cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                stopwatch.Stop();
                _logger.LogWarning(exception, "Endpoint health check failed for source connection {SourceConnectionId}.", sourceConnection.Id);

                await governanceLogger.LogEndpointHealthAsync(
                    new EndpointHealthEntry(
                        sourceConnection.Name,
                        "Source",
                        "Offline",
                        stopwatch.ElapsedMilliseconds,
                        exception.Message),
                    cancellationToken);
            }
        }

        var destinationHealthCheckProviders = scope.ServiceProvider
            .GetServices<IDestinationHealthCheckProvider>()
            .ToDictionary(p => p.DestinationType);
        var destinations = await configurationRepository.GetDestinationsAsync(cancellationToken);

        foreach (var destination in destinations.Where(d => d.IsEnabled))
        {
            if (!destinationHealthCheckProviders.TryGetValue(destination.DestinationType, out var provider))
            {
                // No health-check strategy registered for this destination type (database-direct writers, SFTP) —
                // skip rather than fake a result. See IDestinationHealthCheckProvider's remarks.
                continue;
            }

            var stopwatch = Stopwatch.StartNew();

            try
            {
                var result = await provider.CheckAsync(destination, cancellationToken);
                stopwatch.Stop();

                await governanceLogger.LogEndpointHealthAsync(
                    new EndpointHealthEntry(
                        destination.Name,
                        "Destination",
                        result.IsSuccessful ? "Healthy" : "Offline",
                        stopwatch.ElapsedMilliseconds,
                        result.Message),
                    cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                stopwatch.Stop();
                _logger.LogWarning(exception, "Endpoint health check failed for destination {DestinationId}.", destination.Id);

                await governanceLogger.LogEndpointHealthAsync(
                    new EndpointHealthEntry(
                        destination.Name,
                        "Destination",
                        "Offline",
                        stopwatch.ElapsedMilliseconds,
                        exception.Message),
                    cancellationToken);
            }
        }
    }
}

using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.Abstractions.Pipeline;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Domain.Enums;
using FHIRBridge.Infrastructure.Pipeline;
using Microsoft.Extensions.Options;

namespace FHIRBridge.Worker;

public sealed class Worker : BackgroundService
{
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly IOptions<RuntimeWorkerOptions> _options;
    private readonly ILogger<Worker> _logger;

    public Worker(
        IServiceScopeFactory serviceScopeFactory,
        IOptions<RuntimeWorkerOptions> options,
        ILogger<Worker> logger)
    {
        _serviceScopeFactory = serviceScopeFactory;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Value.Enabled)
        {
            _logger.LogInformation("FHIRBridge runtime worker is disabled. Set RuntimeWorker:Enabled=true to run scheduled Phase 1 jobs.");
            await WaitUntilStoppedAsync(stoppingToken);
            return;
        }

        await RunOnceAsync(stoppingToken);

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(30, _options.Value.IntervalSeconds)));

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await RunOnceAsync(stoppingToken);
        }
    }

    private async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        var options = _options.Value;

        using var scope = _serviceScopeFactory.CreateScope();
        var configurationRepository = scope.ServiceProvider.GetRequiredService<IConfigurationRepository>();
        var nowUtc = DateTime.UtcNow;
        var dueResourceTypes = await GetDueResourceTypesAsync(
            configurationRepository,
            options.ResourceTypes,
            nowUtc,
            cancellationToken);

        if (dueResourceTypes.Count == 0)
        {
            _logger.LogInformation(
                "No configured routes were due at {ScheduledAtUtc}.",
                nowUtc);

            return;
        }

        var pipelineService = scope.ServiceProvider.GetRequiredService<IConfiguredPipelineService>();
        var pipelineRun = await pipelineService.StartAsync(
            new StartConfiguredPipelineRunRequest(
                dueResourceTypes,
                "worker",
                Guid.NewGuid().ToString("N"))
            {
                RunDueSchedulesOnly = true,
                ScheduledAtUtc = nowUtc
            },
            cancellationToken);

        _logger.LogInformation(
            "Scheduled unified pipeline run {PipelineRunId} finished with status {Status}. Extracted {Extracted}; wrote {Written}.",
            pipelineRun.Id,
            pipelineRun.Status,
            pipelineRun.ExtractedResourceCount,
            pipelineRun.WrittenRecordCount);
    }

    private static async Task<IReadOnlyList<string>> GetDueResourceTypesAsync(
        IConfigurationRepository configurationRepository,
        IReadOnlyCollection<string> configuredResourceTypes,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        var routes = await configurationRepository.GetRoutesAsync(cancellationToken);
        var mappings = (await configurationRepository.GetMappingProfilesAsync(cancellationToken))
            .ToDictionary(x => x.Id);
        var sources = (await configurationRepository.GetSourceConnectionsAsync(cancellationToken))
            .ToDictionary(x => x.Id);
        var destinations = (await configurationRepository.GetDestinationsAsync(cancellationToken))
            .ToDictionary(x => x.Id);
        var webhooks = (await configurationRepository.GetWebhooksAsync(cancellationToken))
            .ToDictionary(x => x.Id);

        var requestedResourceTypes = configuredResourceTypes.Count == 0
            ? null
            : configuredResourceTypes.ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Resource type is owned by each route's mapping profile (single source of truth).
        return routes
            .Where(route =>
                route.IsEnabled &&
                RouteDependenciesAreEnabled(route, mappings, sources, destinations, webhooks) &&
                IsScheduledPullMode(route.IngestionMode) &&
                ScheduleExpressionMatcher.IsDue(route.ScheduleExpression, nowUtc))
            .Select(route => mappings.TryGetValue(route.MappingProfileId, out var mapping) ? mapping.ResourceType : null)
            .Where(resourceType => !string.IsNullOrWhiteSpace(resourceType))
            .Select(resourceType => resourceType!)
            .Where(resourceType => requestedResourceTypes is null || requestedResourceTypes.Contains(resourceType))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(resourceType => resourceType)
            .ToList();
    }

    private static bool IsScheduledPullMode(IngestionMode ingestionMode)
    {
        return ingestionMode is IngestionMode.ScheduledPull or IngestionMode.WebhookAndScheduledPull;
    }

    private static bool RouteDependenciesAreEnabled(
        ResourcePipelineRoute route,
        IReadOnlyDictionary<Guid, MappingProfile> mappings,
        IReadOnlyDictionary<Guid, SourceConnection> sources,
        IReadOnlyDictionary<Guid, DestinationConfiguration> destinations,
        IReadOnlyDictionary<Guid, WebhookConfiguration> webhooks)
    {
        // A route's source and destination are owned by its mapping profile, so resolve them through the mapping.
        if (!mappings.TryGetValue(route.MappingProfileId, out var mapping))
        {
            return false;
        }

        sources.TryGetValue(mapping.SourceConnectionId, out var source);
        destinations.TryGetValue(mapping.DestinationId, out var destination);
        WebhookConfiguration? webhook = null;
        if (route.WebhookConfigurationId.HasValue)
        {
            webhooks.TryGetValue(route.WebhookConfigurationId.Value, out webhook);
        }

        return source?.IsEnabled == true &&
               destination?.IsEnabled == true &&
               mapping.IsEnabled &&
               (!route.WebhookConfigurationId.HasValue || webhook?.IsEnabled == true);
    }

    private static async Task WaitUntilStoppedAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
    }
}

using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.Abstractions.Pipeline;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Domain.Aggregates;
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

        if (options.TenantId == Guid.Empty)
        {
            _logger.LogWarning("RuntimeWorker:TenantId is missing. Scheduled pipeline run skipped.");
            return;
        }

        using var scope = _serviceScopeFactory.CreateScope();
        var tenantRepository = scope.ServiceProvider.GetRequiredService<ITenantConfigurationRepository>();
        var nowUtc = DateTime.UtcNow;
        var dueResourceTypes = await GetDueResourceTypesAsync(
            tenantRepository,
            options.TenantId,
            options.ResourceTypes,
            nowUtc,
            cancellationToken);

        if (dueResourceTypes.Count == 0)
        {
            _logger.LogInformation(
                "No configured routes were due for tenant {TenantId} at {ScheduledAtUtc}.",
                options.TenantId,
                nowUtc);

            return;
        }

        var pipelineService = scope.ServiceProvider.GetRequiredService<IConfiguredPipelineService>();
        var pipelineRun = await pipelineService.StartAsync(
            options.TenantId,
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
        ITenantConfigurationRepository tenantRepository,
        Guid tenantId,
        IReadOnlyCollection<string> configuredResourceTypes,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        var tenant = await tenantRepository.GetByIdAsync(tenantId, cancellationToken);
        if (tenant is null)
        {
            return [];
        }

        var requestedResourceTypes = configuredResourceTypes.Count == 0
            ? null
            : configuredResourceTypes.ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Resource type is owned by each route's mapping profile (single source of truth).
        return tenant.ResourcePipelineRoutes
            .Where(route =>
                route.IsEnabled &&
                RouteDependenciesAreEnabled(tenant, route) &&
                IsScheduledPullMode(route.IngestionMode) &&
                ScheduleExpressionMatcher.IsDue(route.ScheduleExpression, nowUtc))
            .Select(route => tenant.ResolveResourceType(route))
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

    private static bool RouteDependenciesAreEnabled(Tenant tenant, ResourcePipelineRoute route)
    {
        // A route's source and destination are owned by its mapping profile, so resolve them through the mapping.
        var mapping = tenant.MappingProfiles.FirstOrDefault(x => x.Id == route.MappingProfileId);
        if (mapping is null)
        {
            return false;
        }

        var source = tenant.SourceConnections.FirstOrDefault(x => x.Id == mapping.SourceConnectionId);
        var destination = tenant.DestinationConfigurations.FirstOrDefault(x => x.Id == mapping.DestinationId);
        var webhook = route.WebhookConfigurationId.HasValue
            ? tenant.WebhookConfigurations.FirstOrDefault(x => x.Id == route.WebhookConfigurationId.Value)
            : null;

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

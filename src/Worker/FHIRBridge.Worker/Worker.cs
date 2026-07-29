using FHIRBridge.Application.Abstractions.Caching;
using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.Abstractions.Pipeline;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Domain.Enums;
using FHIRBridge.Governance;
using FHIRBridge.Infrastructure.Pipeline;
using FHIRBridge.Runtime.Application.Workflows;
using FHIRBridge.Runtime.Application.Workflows.Storage;
using FHIRBridge.Runtime.Domain.Workflows;
using Microsoft.Extensions.Options;

namespace FHIRBridge.Worker;

public sealed class Worker : BackgroundService
{
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly IOptions<RuntimeWorkerOptions> _options;
    private readonly ISystemSettingsCache _settingsCache;
    private readonly ILogger<Worker> _logger;

    public Worker(
        IServiceScopeFactory serviceScopeFactory,
        IOptions<RuntimeWorkerOptions> options,
        ISystemSettingsCache settingsCache,
        ILogger<Worker> logger)
    {
        _serviceScopeFactory = serviceScopeFactory;
        _options = options;
        _settingsCache = settingsCache;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var enabled = await _settingsCache.GetBoolAsync("RuntimeWorker:Enabled", _options.Value.Enabled, stoppingToken);
            if (!enabled)
            {
                _logger.LogInformation("FHIRBridge runtime worker is disabled. Set RuntimeWorker:Enabled=true to run scheduled Phase 1 jobs.");
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
                continue;
            }

            await RunOnceAsync(stoppingToken);

            var intervalSeconds = await _settingsCache.GetIntAsync(
                "RuntimeWorker:IntervalSeconds", _options.Value.IntervalSeconds, stoppingToken);
            await Task.Delay(TimeSpan.FromSeconds(Math.Max(30, intervalSeconds)), stoppingToken);
        }
    }

    private async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        using var scope = _serviceScopeFactory.CreateScope();
        var nowUtc = DateTime.UtcNow;

        // Route scheduling now lives on the queue-based path (ScheduleDispatcherWorker → PipelineRunCommandProcessor)
        // by default — see RuntimeWorkerOptions.DirectRouteSchedulingEnabled's remarks for the fast-rollback story.
        if (_options.Value.DirectRouteSchedulingEnabled)
        {
            await RunDueRoutesAsync(scope, nowUtc, cancellationToken);
        }

        await RunDueWorkflowsAsync(scope, nowUtc, cancellationToken);
    }

    // Direct-call route scheduling — off by default (see RuntimeWorkerOptions.DirectRouteSchedulingEnabled), kept
    // as a fast-rollback path if the queue-based scheduler (ScheduleDispatcherWorker) needs to be disabled without
    // a redeploy. Never run both at once: see ScheduleDispatcher's remarks for why that would double-dispatch.
    private async Task RunDueRoutesAsync(IServiceScope scope, DateTime nowUtc, CancellationToken cancellationToken)
    {
        var options = _options.Value;
        var governanceLogger = scope.ServiceProvider.GetRequiredService<IGovernanceLogger>();
        var configurationRepository = scope.ServiceProvider.GetRequiredService<IConfigurationRepository>();
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

        var correlationId = Guid.NewGuid().ToString("N");

        await governanceLogger.LogSchedulerRunAsync(
            new SchedulerRunEntry("worker:poll", "Dispatched", dueResourceTypes.Count, correlationId),
            cancellationToken);

        var pipelineService = scope.ServiceProvider.GetRequiredService<IConfiguredPipelineService>();
        var ambientActorContext = scope.ServiceProvider.GetRequiredService<IAmbientActorContext>();

        try
        {
            using var actorScope = ambientActorContext.BeginScope("Scheduler (Legacy Poll)", correlationId);

            var pipelineRun = await pipelineService.StartAsync(
                new StartConfiguredPipelineRunRequest(
                    dueResourceTypes,
                    "worker",
                    correlationId)
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
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            // Previously unhandled here: an exception would propagate out of ExecuteAsync and permanently fault
            // this BackgroundService, silently stopping all future scheduled runs. Logging and continuing matches
            // RunDueWorkflowsAsync's existing per-item resilience below.
            _logger.LogError(exception, "Scheduled unified pipeline run failed at {ScheduledAtUtc}.", nowUtc);
            var exceptionManager = scope.ServiceProvider.GetRequiredService<IGlobalExceptionManager>();
            await exceptionManager.CaptureAsync(
                exception,
                new ExceptionContext(Module: "Scheduler", CorrelationId: correlationId),
                CancellationToken.None);
        }
    }

    // Approach-B workflow scheduling: fire enabled workflow graphs whose trigger is due. Graceful when the graph
    // engine isn't composed in this host (GetService → null), so route scheduling is unaffected.
    private async Task RunDueWorkflowsAsync(IServiceScope scope, DateTime nowUtc, CancellationToken cancellationToken)
    {
        var store = scope.ServiceProvider.GetService<IWorkflowDefinitionStore>();
        var orchestrator = scope.ServiceProvider.GetService<IRankedWorkflowOrchestrator>();
        if (store is null || orchestrator is null)
        {
            return;
        }

        var governanceLogger = scope.ServiceProvider.GetRequiredService<IGovernanceLogger>();
        var ambientActorContext = scope.ServiceProvider.GetRequiredService<IAmbientActorContext>();
        var workflows = await store.ListAsync(cancellationToken);
        foreach (var workflow in workflows)
        {
            if (!workflow.IsEnabled || workflow.Trigger is null || !IsWorkflowDue(workflow, nowUtc))
            {
                continue;
            }

            var correlationId = Guid.NewGuid().ToString("N");

            await governanceLogger.LogSchedulerRunAsync(
                new SchedulerRunEntry($"Scheduler (Workflow: {workflow.Name})", "Dispatched", 1, correlationId),
                cancellationToken);

            try
            {
                using var actorScope = ambientActorContext.BeginScope($"Scheduler (Workflow: {workflow.Name})", correlationId);

                var context = new WorkflowExecutionContext(
                    Guid.NewGuid(),
                    correlationId,
                    triggeredBy: "scheduler",
                    triggerType: "Scheduled");
                var result = await orchestrator.ExecuteAsync(workflow, context, cancellationToken);
                workflow.MarkTriggered(nowUtc);
                await store.SaveAsync(workflow, cancellationToken);

                _logger.LogInformation(
                    "Scheduled workflow {WorkflowId} '{Name}' fired at {ScheduledAtUtc} → {Status}.",
                    workflow.Id, workflow.Name, nowUtc, result.WorkflowRun.Status);
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogError(exception, "Scheduled workflow {WorkflowId} '{Name}' failed.", workflow.Id, workflow.Name);
                var exceptionManager = scope.ServiceProvider.GetRequiredService<IGlobalExceptionManager>();
                await exceptionManager.CaptureAsync(
                    exception,
                    new ExceptionContext(Module: "Scheduler", CorrelationId: correlationId),
                    CancellationToken.None);
            }
        }
    }

    private static bool IsWorkflowDue(WorkflowDefinition workflow, DateTime nowUtc)
    {
        var trigger = workflow.Trigger!;
        return trigger.Type switch
        {
            WorkflowTriggerType.Schedule =>
                ScheduleExpressionMatcher.IsDueSince(trigger.ScheduleExpression, workflow.LastTriggeredOnUtc, nowUtc),
            WorkflowTriggerType.Poll =>
                trigger.IntervalMinutes is int minutes && minutes > 0 &&
                (workflow.LastTriggeredOnUtc is null ||
                 nowUtc - workflow.LastTriggeredOnUtc.Value >= TimeSpan.FromMinutes(minutes)),
            _ => false,
        };
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

}

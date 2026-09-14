using FHIRBridge.Application.Abstractions.Caching;
using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.Abstractions.Pipeline;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Domain.Enums;
using FHIRBridge.Governance;
using FHIRBridge.Infrastructure.Pipeline;
using FHIRBridge.Infrastructure.Security;
using FHIRBridge.Observability.Logging;
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
            // Defaults to true, matching the seeded SystemSettings value and ScheduleDispatcherOptions.Enabled.
            // This is the fallback used only when SystemSettings can't be read at all (see
            // InProcessSystemSettingsCache) — a false default there made a settings-read failure indistinguishable
            // from a deliberate opt-out, silently stopping every scheduled workflow.
            var enabled = await _settingsCache.GetBoolAsync("RuntimeWorker:Enabled", defaultValue: true, stoppingToken);
            if (!enabled)
            {
                _logger.LogInformation(
                    LogEvents.SchedulerDisabled,
                    "FHIRBridge runtime worker is disabled. Set RuntimeWorker:Enabled=true to run scheduled Phase 1 jobs.");
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
            using var actorScope = ambientActorContext.BeginCorrelatedScope("Scheduler (Legacy Poll)", correlationId);

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
        var dueCount = 0;
        foreach (var workflow in workflows)
        {
            if (!workflow.IsEnabled || workflow.Trigger is null || !IsWorkflowDue(workflow, nowUtc))
            {
                continue;
            }

            dueCount++;
            var correlationId = Guid.NewGuid().ToString("N");

            // Emitted before the run is attempted, so the schedule that selected this workflow is on record even if
            // the execution below throws. TimeZone* come from DescribeTimeZone rather than the raw trigger value: an
            // id this host can't resolve silently degrades to UTC (shifting every fire by the zone's offset), and
            // TimeZoneResolved=false is the only signal anywhere that this happened.
            var timeZone = ScheduleExpressionMatcher.DescribeTimeZone(workflow.Trigger.TimeZoneId);
            _logger.LogInformation(
                LogEvents.WorkflowScheduleDue,
                "Workflow {WorkflowId} '{WorkflowName}' is due at {ScheduledAtUtc:o}. " +
                "Trigger={TriggerType} Schedule={ScheduleExpression} IntervalMinutes={IntervalMinutes} " +
                "TimeZoneId={TimeZoneId} TimeZoneResolved={TimeZoneResolved} TimeZoneOffset={TimeZoneOffset} " +
                "LastTriggeredOnUtc={LastTriggeredOnUtc} CorrelationId={CorrelationId}",
                workflow.Id, workflow.Name, nowUtc,
                workflow.Trigger.Type, workflow.Trigger.ScheduleExpression, workflow.Trigger.IntervalMinutes,
                timeZone.RequestedId, timeZone.Resolved, timeZone.BaseUtcOffset,
                workflow.LastTriggeredOnUtc, correlationId);

            if (!timeZone.Resolved)
            {
                // Warning, not Error: the run still happens, just at the wrong wall-clock time.
                _logger.LogWarning(
                    LogEvents.TimeZoneResolutionFailed,
                    "Workflow {WorkflowId} '{WorkflowName}' is scheduled in time zone '{TimeZoneId}', which this host " +
                    "could not resolve — the schedule is being evaluated in UTC instead, so it fires at the wrong " +
                    "local time. Install the host's tzdata/ICU zone data, or pick a zone id this host knows.",
                    workflow.Id, workflow.Name, timeZone.RequestedId);
            }

            await governanceLogger.LogSchedulerRunAsync(
                new SchedulerRunEntry($"Scheduler (Workflow: {workflow.Name})", "Dispatched", 1, correlationId),
                cancellationToken);

            try
            {
                using var actorScope = ambientActorContext.BeginCorrelatedScope($"Scheduler (Workflow: {workflow.Name})", correlationId);

                var context = new WorkflowExecutionContext(
                    Guid.NewGuid(),
                    correlationId,
                    triggeredBy: "scheduler",
                    triggerType: "Scheduled");
                var startedAt = System.Diagnostics.Stopwatch.GetTimestamp();
                var result = await orchestrator.ExecuteAsync(workflow, context, cancellationToken);
                workflow.MarkTriggered(nowUtc);
                await store.SaveAsync(workflow, cancellationToken);

                _logger.LogInformation(
                    LogEvents.SchedulerTickCompleted,
                    "Scheduled workflow {WorkflowId} '{Name}' fired at {ScheduledAtUtc} → {Status} in {ElapsedMs}ms. " +
                    "WorkflowRunId={WorkflowRunId} CorrelationId={CorrelationId}",
                    workflow.Id, workflow.Name, nowUtc, result.WorkflowRun.Status,
                    (long)System.Diagnostics.Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds,
                    result.WorkflowRun.Id, correlationId);
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                // MarkTriggered above is deliberately not reached on a failure, so this workflow stays due and
                // re-fires on the next tick until its slot leaves the catch-up window — expect repeats of this event.
                _logger.LogError(
                    LogEvents.WorkflowRunFailed,
                    exception,
                    "Scheduled workflow {WorkflowId} '{Name}' failed after {ScheduledAtUtc}; it stays due and will " +
                    "retry on the next tick. CorrelationId={CorrelationId}",
                    workflow.Id, workflow.Name, nowUtc, correlationId);
                var exceptionManager = scope.ServiceProvider.GetRequiredService<IGlobalExceptionManager>();
                await exceptionManager.CaptureAsync(
                    exception,
                    new ExceptionContext(Module: "Scheduler", CorrelationId: correlationId),
                    CancellationToken.None);
            }
        }

        // Debug, not Information: this fires on every tick (every 5 minutes in a deployed host) and would otherwise
        // dominate the log. Raise FHIRBridge.Worker to Debug when diagnosing "my schedule never ran" — a tick
        // reporting Due=0 against a non-zero Enabled count means the trigger isn't matching, not that the worker
        // is asleep, which is otherwise indistinguishable from the outside.
        _logger.LogDebug(
            LogEvents.WorkflowScheduleEvaluated,
            "Workflow schedule evaluation at {ScheduledAtUtc}: {WorkflowCount} defined, {EnabledCount} enabled, {DueCount} due.",
            nowUtc, workflows.Count, workflows.Count(w => w.IsEnabled && w.Trigger is not null), dueCount);
    }

    private static bool IsWorkflowDue(WorkflowDefinition workflow, DateTime nowUtc)
    {
        var trigger = workflow.Trigger!;
        return trigger.Type switch
        {
            WorkflowTriggerType.Schedule =>
                ScheduleExpressionMatcher.IsDueSince(trigger.ScheduleExpression, workflow.LastTriggeredOnUtc, nowUtc, trigger.TimeZoneId, workflow.CreatedOnUtc),
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
                ScheduleExpressionMatcher.IsDue(route.ScheduleExpression, nowUtc, route.TimeZoneId))
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

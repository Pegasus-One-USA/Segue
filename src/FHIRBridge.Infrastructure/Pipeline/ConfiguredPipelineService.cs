using FHIRBridge.Application.Abstractions.Audit;
using FHIRBridge.Application.Abstractions.Destinations;
using FHIRBridge.Application.Abstractions.Governance;
using FHIRBridge.Application.Abstractions.Mapping;
using FHIRBridge.Application.Abstractions.Normalization;
using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.Abstractions.Pipeline;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Application.Mappings;
using FHIRBridge.Application.Services;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Domain.Enums;
using FHIRBridge.Domain.Fhir;
using FHIRBridge.Integration.Fhir;
using FHIRBridge.Runtime.Application.Abstractions.Connectors;
using FHIRBridge.Runtime.Application.DTOs;
using FHIRBridge.Runtime.Domain.Enums;
using FHIRBridge.Runtime.Domain.ValueObjects;
using FHIRBridge.SharedKernel.Exceptions;
using FHIRBridge.SharedKernel.Observability;
using Microsoft.Extensions.Logging;
using System.Text.Json.Nodes;

namespace FHIRBridge.Infrastructure.Pipeline;

public sealed class ConfiguredPipelineService : IConfiguredPipelineService
{
    private readonly IConfigurationRepository _configurationRepository;
    private readonly IFhirSourceClientFactory _sourceClientFactory;
    private readonly IJsonMappingEngine _mappingEngine;
    private readonly IConfiguredDestinationWriterFactory _destinationWriterFactory;
    private readonly ISecretProvider _secretProvider;
    private readonly IOperationalAuditService _auditService;
    private readonly IConfiguredPipelineRunRepository _pipelineRunRepository;
    private readonly IPipelineRunRouteExecutionRepository _routeExecutionRepository;
    private readonly IExecutionResourceHistoryRecorder _resourceHistoryRecorder;
    private readonly IResourceNormalizationService _normalizationService;
    private readonly IMappedRecordNormalizationService _mappedRecordNormalizationService;
    private readonly IGovernancePolicyService _governancePolicyService;
    private readonly IDeIdentificationService _deIdentificationService;
    private readonly IDataSetDeIdentificationService? _dataSetDeIdentificationService;
    private readonly ILineageTracker _lineageTracker;
    private readonly IFhirBulkExportClient? _bulkExportClient;
    private readonly IPipelineMetrics? _pipelineMetrics;
    private readonly IncrementalSyncOptions _incrementalSyncOptions;
    private readonly ILogger<ConfiguredPipelineService> _logger;

    public ConfiguredPipelineService(
        IConfigurationRepository configurationRepository,
        IFhirSourceClientFactory sourceClientFactory,
        IJsonMappingEngine mappingEngine,
        IConfiguredDestinationWriterFactory destinationWriterFactory,
        ISecretProvider secretProvider,
        IOperationalAuditService auditService,
        IConfiguredPipelineRunRepository pipelineRunRepository,
        IPipelineRunRouteExecutionRepository routeExecutionRepository,
        IExecutionResourceHistoryRecorder resourceHistoryRecorder,
        ILogger<ConfiguredPipelineService> logger,
        IResourceNormalizationService? normalizationService = null,
        IMappedRecordNormalizationService? mappedRecordNormalizationService = null,
        IGovernancePolicyService? governancePolicyService = null,
        IDeIdentificationService? deIdentificationService = null,
        ILineageTracker? lineageTracker = null,
        IFhirBulkExportClient? bulkExportClient = null,
        IPipelineMetrics? pipelineMetrics = null,
        IncrementalSyncOptions? incrementalSyncOptions = null,
        IDataSetDeIdentificationService? dataSetDeIdentificationService = null)
    {
        _configurationRepository = configurationRepository;
        _sourceClientFactory = sourceClientFactory;
        _mappingEngine = mappingEngine;
        _destinationWriterFactory = destinationWriterFactory;
        _secretProvider = secretProvider;
        _auditService = auditService;
        _pipelineRunRepository = pipelineRunRepository;
        _routeExecutionRepository = routeExecutionRepository;
        _resourceHistoryRecorder = resourceHistoryRecorder;
        _normalizationService = normalizationService ?? new PassThroughResourceNormalizationService();
        _mappedRecordNormalizationService = mappedRecordNormalizationService ?? new PassThroughMappedRecordNormalizationService();
        _governancePolicyService = governancePolicyService ?? new DefaultGovernancePolicyService();
        _deIdentificationService = deIdentificationService ?? new PassThroughDeIdentificationService();
        _lineageTracker = lineageTracker ?? new NoOpLineageTracker();
        _bulkExportClient = bulkExportClient;
        _pipelineMetrics = pipelineMetrics;
        _incrementalSyncOptions = incrementalSyncOptions ?? IncrementalSyncOptions.Default;
        _dataSetDeIdentificationService = dataSetDeIdentificationService;
        _logger = logger;
    }

    public Task<IReadOnlyList<ConfiguredPipelineRunDto>> GetRecentAsync(
        int count,
        CancellationToken cancellationToken)
    {
        return _pipelineRunRepository.GetRecentAsync(count, cancellationToken);
    }

    public async Task SetRunEnabledAsync(
        Guid pipelineRunId,
        bool isEnabled,
        CancellationToken cancellationToken)
    {
        await _pipelineRunRepository.SetEnabledAsync(
            pipelineRunId,
            isEnabled,
            cancellationToken);

        await RecordAuditAsync(
            pipelineRunId,
            null,
            null,
            null,
            null,
            null,
            isEnabled ? "PipelineRunActivated" : "PipelineRunDeactivated",
            "Completed",
            $"Pipeline run {pipelineRunId} was {(isEnabled ? "activated" : "deactivated")}.",
            null,
            null,
            null,
            cancellationToken);
    }

    public async Task<ConfiguredPipelineRunDto> StartAsync(
        StartConfiguredPipelineRunRequest request,
        CancellationToken cancellationToken)
    {
        var startedOnUtc = DateTime.UtcNow;
        var pipelineRunId = Guid.NewGuid();
        var errors = new List<string>();
        var extractedCount = 0;
        var mappedCount = 0;
        var writtenCount = 0;

        var config = await LoadConfigurationAsync(cancellationToken);
        var scheduledAtUtc = request.ScheduledAtUtc ?? DateTime.UtcNow;

        // TriggerType is derived from the request shape rather than trusting a caller-supplied value, so it can
        // never drift from TriggeredBy the way it used to (see CompleteRunAsync's old TriggerType: triggeredBy bug).
        var triggerType = request.UseBulkExport
            ? "Bulk"
            : request.RunDueSchedulesOnly
                ? "Scheduled"
                : "Manual";

        await RecordAuditAsync(
            pipelineRunId,
            null,
            null,
            null,
            null,
            null,
            "PipelineRunStarted",
            "Started",
            "Configured scheduled/manual pipeline run started.",
            null,
            request.TriggeredBy,
            request.CorrelationId,
            cancellationToken);

        var routesByResourceType = ResolveRoutesByResourceType(config, request.ResourceTypes);
        var processedResourceTypes = new List<string>();

        foreach (var (resourceType, routesForType) in routesByResourceType)
        {
            processedResourceTypes.Add(resourceType);

            var enabledRoutes = routesForType
                .Where(route => route.Route.IsEnabled)
                .Where(route => route.IsEnabled)
                .Where(route => RouteDependenciesAreEnabled(config, route))
                .Where(route => IsScheduledPullMode(route.Route.IngestionMode))
                .Where(route => request.RouteIds is not null
                    ? request.RouteIds.Contains(route.Route.Id)
                    : !request.RunDueSchedulesOnly ||
                      ScheduleExpressionMatcher.IsDue(route.Route.ScheduleExpression, scheduledAtUtc))
                .OrderBy(route => route.Route.Priority)
                .ThenBy(route => route.Route.Id)
                .ThenBy(route => route.ExecutionOrder)
                .ToList();

            if (enabledRoutes.Count == 0)
            {
                if (!request.RunDueSchedulesOnly)
                {
                    errors.Add($"{resourceType}: No enabled scheduled pipeline routes were found.");
                }

                continue;
            }

            foreach (var routeGroup in enabledRoutes.GroupBy(route => CreateRouteSourceKey(config, route)))
            {
                IReadOnlyList<ResourceEnvelope> resources;

                try
                {
                    var sourceConnection = GetRequired(
                        config.SourceConnections,
                        routeGroup.Key.SourceConnectionId,
                        "SourceConnection");

                    await RecordAuditAsync(
                        pipelineRunId,
                        null,
                        sourceConnection.Id,
                        null,
                        null,
                        resourceType,
                        "SourceExtractionStarted",
                        "Started",
                        "FHIR source extraction started.",
                        null,
                        request.TriggeredBy,
                        request.CorrelationId,
                        cancellationToken);

                    // For the search path, make the pull incremental ("since last run") unless bulk export is used
                    // (bulk $export uses _since, not _lastUpdated) or the route already pins _lastUpdated.
                    var effectiveSearchParameters = request.UseBulkExport
                        ? routeGroup.Key.SearchParameters
                        : await ApplyIncrementalFilterAsync(
                            resourceType,
                            routeGroup.Key.SearchParameters,
                            cancellationToken);

                    var sourceConfiguration = await BuildSourceConfigurationAsync(
                        sourceConnection,
                        effectiveSearchParameters,
                        cancellationToken);

                    if (request.UseBulkExport)
                    {
                        // Bulk Data $export path: scope the export to this resource type, reuse the rest of the pipeline.
                        if (_bulkExportClient is null)
                        {
                            throw new InvalidOperationException("Bulk export was requested but no bulk export client is configured.");
                        }

                        resources = await _bulkExportClient.ExportAsync(
                            new FhirBulkExportRequest(
                                BulkExportScope.System,
                                ResourceTypes: [resourceType]),
                            sourceConfiguration,
                            cancellationToken);
                    }
                    else
                    {
                        var sourceClient = _sourceClientFactory.Create(sourceConfiguration.SourceType);
                        resources = await sourceClient.SearchAsync(
                            resourceType,
                            sourceConfiguration,
                            cancellationToken);
                    }

                    extractedCount += resources.Count;

                    // Expert Determination (k-anonymity) set-level de-identification across the extracted cohort.
                    // No-op passthrough unless explicitly enabled in configuration.
                    resources = await ApplyDataSetDeIdentificationAsync(
                        pipelineRunId,
                        resourceType,
                        resources,
                        request.TriggeredBy,
                        request.CorrelationId,
                        cancellationToken);

                    await RecordAuditAsync(
                        pipelineRunId,
                        null,
                        sourceConnection.Id,
                        null,
                        null,
                        resourceType,
                        "SourceExtractionCompleted",
                        "Completed",
                        "FHIR source extraction completed.",
                        resources.Count,
                        request.TriggeredBy,
                        request.CorrelationId,
                        cancellationToken);
                }
                catch (Exception exception)
                {
                    _logger.LogError(
                        exception,
                        "Configured pipeline source extraction failed for resource {ResourceType}, source {SourceConnectionId}.",
                        resourceType,
                        routeGroup.Key.SourceConnectionId);

                    errors.Add($"{resourceType}/{routeGroup.Key.SourceConnectionId}: {exception.Message}");

                    await RecordAuditAsync(
                        pipelineRunId,
                        null,
                        routeGroup.Key.SourceConnectionId,
                        null,
                        null,
                        resourceType,
                        "SourceExtractionFailed",
                        "Failed",
                        exception.Message,
                        null,
                        request.TriggeredBy,
                        request.CorrelationId,
                        cancellationToken);

                    continue;
                }

                foreach (var route in routeGroup)
                {
                    var result = await ExecuteRouteAsync(
                        config,
                        pipelineRunId,
                        resourceType,
                        route,
                        resources,
                        request.TriggeredBy,
                        triggerType,
                        request.CorrelationId,
                        errors,
                        cancellationToken);

                    mappedCount += result.MappedCount;
                    writtenCount += result.WrittenCount;
                }
            }
        }

        return await CompleteRunAsync(
            pipelineRunId,
            processedResourceTypes,
            extractedCount,
            mappedCount,
            writtenCount,
            errors,
            startedOnUtc,
            request.TriggeredBy,
            triggerType,
            request.CorrelationId,
            cancellationToken);
    }

    public async Task<ConfiguredPipelineRunDto> StartWebhookAsync(
        Guid webhookConfigurationId,
        WebhookIngestionRequest request,
        CancellationToken cancellationToken)
    {
        var startedOnUtc = DateTime.UtcNow;
        var pipelineRunId = Guid.NewGuid();
        var errors = new List<string>();
        var mappedCount = 0;
        var writtenCount = 0;

        var config = await LoadConfigurationAsync(cancellationToken);
        var webhookConfiguration = GetRequired(
            config.WebhookConfigurations,
            webhookConfigurationId,
            "WebhookConfiguration");

        if (!webhookConfiguration.IsEnabled)
        {
            throw new InvalidOperationException("Webhook configuration is disabled.");
        }

        var webhookSourceConnection = GetRequired(
            config.SourceConnections,
            webhookConfiguration.SourceConnectionId,
            "SourceConnection");
        if (!webhookSourceConnection.IsEnabled)
        {
            throw new InvalidOperationException("Webhook source connection is disabled.");
        }

        var resourcesByType = FhirResourceParser.ParsePayload(request.ResourceJson)
            .GroupBy(resource => SupportedFhirResourceTypes.Normalize(resource.ResourceType))
            .ToList();
        var extractedCount = resourcesByType.Sum(group => group.Count());
        var processedResourceTypes = new List<string>();
        const string triggerType = "Webhook";

        await RecordAuditAsync(
            pipelineRunId,
            null,
            webhookConfiguration.SourceConnectionId,
            null,
            null,
            null,
            "WebhookPayloadReceived",
            "Completed",
            "FHIR webhook payload received.",
            extractedCount,
            request.TriggeredBy,
            request.CorrelationId,
            cancellationToken);

        foreach (var resourcesForType in resourcesByType)
        {
            var resourceType = resourcesForType.Key;
            processedResourceTypes.Add(resourceType);

            // Match routes whose mapping resolves to the incoming resource type (single source of truth).
            var webhookRoutes = ExpandRouteMappingWorkItems(config)
                .Where(route => string.Equals(route.MappingProfile.ResourceType, resourceType, StringComparison.OrdinalIgnoreCase))
                .Where(route => route.Route.IsEnabled)
                .Where(route => route.IsEnabled)
                .Where(route => RouteDependenciesAreEnabled(config, route))
                .Where(route => route.Route.WebhookConfigurationId == webhookConfigurationId)
                .Where(route => IsWebhookMode(route.Route.IngestionMode))
                .OrderBy(route => route.Route.Priority)
                .ThenBy(route => route.Route.Id)
                .ThenBy(route => route.ExecutionOrder)
                .ToList();

            if (webhookRoutes.Count == 0)
            {
                errors.Add($"{resourceType}: No enabled webhook pipeline routes were found.");
                continue;
            }

            foreach (var route in webhookRoutes)
            {
                var result = await ExecuteRouteAsync(
                    config,
                    pipelineRunId,
                    resourceType,
                    route,
                    resourcesForType.ToList(),
                    request.TriggeredBy,
                    triggerType,
                    request.CorrelationId,
                    errors,
                    cancellationToken);

                mappedCount += result.MappedCount;
                writtenCount += result.WrittenCount;
            }
        }

        return await CompleteRunAsync(
            pipelineRunId,
            processedResourceTypes,
            extractedCount,
            mappedCount,
            writtenCount,
            errors,
            startedOnUtc,
            request.TriggeredBy,
            triggerType,
            request.CorrelationId,
            cancellationToken);
    }

    private async Task<RouteExecutionResult> ExecuteRouteAsync(
        ConfigurationSnapshot config,
        Guid pipelineRunId,
        string resourceType,
        RouteMappingWorkItem route,
        IReadOnlyCollection<ResourceEnvelope> resources,
        string? triggeredBy,
        string? triggerType,
        string? correlationId,
        List<string> errors,
        CancellationToken cancellationToken)
    {
        // Source, destination, and resource type are all owned by the route's mapping profile.
        var mappingProfile = route.MappingProfile;
        // Already resolved once (successfully) to extract `resources`, so this is guaranteed to exist here.
        var sourceConnection = GetRequired(
            config.SourceConnections,
            mappingProfile.SourceConnectionId,
            "SourceConnection");

        // One durable row per route execution — the Execution History screen's display grain. Name/source are
        // snapshotted now so history still reads correctly if the mapping profile or source is later renamed.
        var routeExecutionId = await _routeExecutionRepository.CreateRunningAsync(
            pipelineRunId,
            route.Route.Id,
            mappingProfile.Id,
            mappingProfile.Name,
            sourceConnection.Id,
            sourceConnection.Name,
            sourceConnection.SourceSystemType.ToString(),
            triggeredBy,
            triggerType,
            DateTime.UtcNow,
            cancellationToken);

        // Errors accumulated by THIS route only — `errors` is shared across every route in the whole run, so a
        // before/after count is how we isolate this route's outcome for the route-execution status.
        var errorCountBefore = errors.Count;

        try
        {
            if (!RouteDependenciesAreEnabled(config, route))
            {
                await RecordAuditAsync(
                    pipelineRunId,
                    route.Route.Id,
                    mappingProfile.SourceConnectionId,
                    mappingProfile.DestinationId,
                    mappingProfile.Id,
                    resourceType,
                    "RouteExecutionSkipped",
                    "Skipped",
                    "Resource pipeline route was skipped because one or more lifecycle dependencies are disabled.",
                    resources.Count,
                    triggeredBy,
                    correlationId,
                    cancellationToken);

                await _routeExecutionRepository.CompleteAsync(
                    routeExecutionId,
                    PipelineRunRouteExecutionStatus.Skipped,
                    0,
                    0,
                    0,
                    "Skipped: one or more lifecycle dependencies are disabled.",
                    DateTime.UtcNow,
                    cancellationToken);

                return new RouteExecutionResult(0, 0);
            }

            var destination = GetRequired(
                config.DestinationConfigurations,
                mappingProfile.DestinationId,
                "DestinationConfiguration");

            await RecordAuditAsync(
                pipelineRunId,
                route.Route.Id,
                mappingProfile.SourceConnectionId,
                destination.Id,
                mappingProfile.Id,
                resourceType,
                "RouteExecutionStarted",
                "Started",
                "Resource pipeline route execution started.",
                resources.Count,
                triggeredBy,
                correlationId,
                cancellationToken);

            var governedResources = await PrepareResourcesForRouteAsync(
                pipelineRunId,
                routeExecutionId,
                route.Route,
                destination,
                mappingProfile,
                resourceType,
                resources,
                triggeredBy,
                correlationId,
                errors,
                cancellationToken);

            var mappedRecords = await MapResourcesAsync(
                pipelineRunId,
                routeExecutionId,
                mappingProfile,
                governedResources,
                errors,
                cancellationToken);

            var destinationWriter = _destinationWriterFactory.Create(destination.DestinationType);
            var writtenCount = await destinationWriter.WriteAsync(
                destination,
                mappingProfile,
                mappedRecords,
                cancellationToken);

            if (writtenCount > 0)
            {
                await _resourceHistoryRecorder.RecordStoredAsync(
                    routeExecutionId,
                    mappedRecords.Select(record => record.SourceResourceId).ToList(),
                    cancellationToken);
            }

            await RecordAuditAsync(
                pipelineRunId,
                route.Route.Id,
                mappingProfile.SourceConnectionId,
                destination.Id,
                mappingProfile.Id,
                resourceType,
                "RouteExecutionCompleted",
                "Completed",
                "Resource pipeline route execution completed.",
                writtenCount,
                triggeredBy,
                correlationId,
                cancellationToken);

            var routeHadErrors = errors.Count > errorCountBefore;
            var routeStatus = !routeHadErrors
                ? PipelineRunRouteExecutionStatus.Completed
                : writtenCount > 0
                    ? PipelineRunRouteExecutionStatus.CompletedWithErrors
                    : PipelineRunRouteExecutionStatus.Failed;

            await _routeExecutionRepository.CompleteAsync(
                routeExecutionId,
                routeStatus,
                resources.Count,
                mappedRecords.Count,
                writtenCount,
                routeHadErrors ? string.Join("; ", errors.Skip(errorCountBefore)) : null,
                DateTime.UtcNow,
                cancellationToken);

            return new RouteExecutionResult(mappedRecords.Count, writtenCount);
        }
        catch (Exception exception)
        {
            _logger.LogError(
                exception,
                "Configured pipeline route failed for resource {ResourceType}, route {RouteId}.",
                resourceType,
                route.Route.Id);

            errors.Add($"{resourceType}/route/{route.Route.Id}: {exception.Message}");

            await RecordAuditAsync(
                pipelineRunId,
                route.Route.Id,
                mappingProfile.SourceConnectionId,
                mappingProfile.DestinationId,
                mappingProfile.Id,
                resourceType,
                "RouteExecutionFailed",
                "Failed",
                exception.Message,
                null,
                triggeredBy,
                correlationId,
                cancellationToken);

            await _routeExecutionRepository.CompleteAsync(
                routeExecutionId,
                PipelineRunRouteExecutionStatus.Failed,
                resources.Count,
                0,
                0,
                exception.Message,
                DateTime.UtcNow,
                cancellationToken);

            return new RouteExecutionResult(0, 0);
        }
    }

    private async Task<IReadOnlyCollection<ResourceEnvelope>> PrepareResourcesForRouteAsync(
        Guid pipelineRunId,
        Guid routeExecutionId,
        ResourcePipelineRoute route,
        DestinationConfiguration destination,
        MappingProfile mappingProfile,
        string resourceType,
        IReadOnlyCollection<ResourceEnvelope> resources,
        string? triggeredBy,
        string? correlationId,
        List<string> errors,
        CancellationToken cancellationToken)
    {
        var preparedResources = new List<ResourceEnvelope>();

        foreach (var resource in resources)
        {
            // Recorded before governance runs — the resource genuinely was fetched from source regardless of
            // whether it's ultimately allowed through, and later stages update this same row in place.
            await _resourceHistoryRecorder.RecordFetchedAsync(
                routeExecutionId, resourceType, resource.ResourceId, resource.RawJson, cancellationToken);

            var governanceDecision = await _governancePolicyService.EvaluateAsync(
                new ResourceGovernanceContext(
                    pipelineRunId,
                    route.Id,
                    resourceType,
                    resource.ResourceId,
                    "RouteResourceAccess",
                    triggeredBy,
                    correlationId),
                cancellationToken);

            if (!governanceDecision.IsAllowed)
            {
                var denialReason = governanceDecision.DenialReason ?? "Governance policy denied resource access.";
                errors.Add($"{resourceType}/{resource.ResourceId ?? "unknown"}: Governance denied resource access. {governanceDecision.DenialReason}");

                await RecordAuditAsync(
                    pipelineRunId,
                    route.Id,
                    mappingProfile.SourceConnectionId,
                    destination.Id,
                    mappingProfile.Id,
                    resourceType,
                    "ResourceAccessDenied",
                    "Denied",
                    denialReason,
                    1,
                    triggeredBy,
                    correlationId,
                    cancellationToken);

                await _resourceHistoryRecorder.RecordFailedAsync(
                    routeExecutionId, resourceType, resource.ResourceId, denialReason, cancellationToken);

                continue;
            }

            var normalizationResult = await _normalizationService.NormalizeAsync(
                new ResourceNormalizationRequest(
                    pipelineRunId,
                    resourceType,
                    resource.ResourceId,
                    resource.RawJson),
                cancellationToken);
            var governedJson = normalizationResult.NormalizedJson;

            await _resourceHistoryRecorder.RecordNormalizedAsync(
                routeExecutionId,
                resourceType,
                resource.ResourceId,
                governedJson,
                normalizationResult.AppliedProfiles,
                normalizationResult.Warnings,
                normalizationResult.DataQualityScore,
                normalizationResult.MasterPatientId,
                cancellationToken);

            await RecordLineageAsync(
                pipelineRunId, route, destination, mappingProfile, resourceType, resource.ResourceId,
                "ResourceNormalized", cancellationToken);

            if (governanceDecision.RequiresDeIdentification)
            {
                governedJson = await _deIdentificationService.DeIdentifyAsync(
                    new DeIdentificationRequest(
                        resourceType,
                        resource.ResourceId,
                        governedJson,
                        governanceDecision.AppliedPolicies),
                    cancellationToken);

                await RecordLineageAsync(
                    pipelineRunId, route, destination, mappingProfile, resourceType, resource.ResourceId,
                    "ResourceDeIdentified", cancellationToken);
            }

            preparedResources.Add(resource with { RawJson = governedJson });

            await RecordLineageAsync(
                pipelineRunId, route, destination, mappingProfile, resourceType, resource.ResourceId,
                "ResourceAccessed", cancellationToken);
        }

        return preparedResources;
    }

    private async Task<ConfiguredPipelineRunDto> CompleteRunAsync(
        Guid pipelineRunId,
        IReadOnlyList<string> resourceTypes,
        int extractedCount,
        int mappedCount,
        int writtenCount,
        IReadOnlyList<string> errors,
        DateTime startedOnUtc,
        string? triggeredBy,
        string? triggerType,
        string? correlationId,
        CancellationToken cancellationToken)
    {
        var status = errors.Count == 0
            ? "Completed"
            : writtenCount > 0
                ? "CompletedWithErrors"
                : "Failed";

        await RecordAuditAsync(
            pipelineRunId,
            null,
            null,
            null,
            null,
            null,
            "PipelineRunCompleted",
            status,
            $"Configured pipeline run completed with status {status}.",
            writtenCount,
            triggeredBy,
            correlationId,
            cancellationToken);

        var completedOnUtc = DateTime.UtcNow;

        var pipelineRun = new ConfiguredPipelineRunDto(
            pipelineRunId,
            status,
            resourceTypes.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x).ToList(),
            extractedCount,
            mappedCount,
            writtenCount,
            errors,
            startedOnUtc,
            completedOnUtc,
            IsEnabled: true,
            TriggeredBy: triggeredBy,
            TriggerType: triggerType);

        await _pipelineRunRepository.AddAsync(pipelineRun, cancellationToken);

        _pipelineMetrics?.RecordRun(new PipelineRunMetric(
            status,
            extractedCount,
            mappedCount,
            writtenCount,
            errors.Count,
            completedOnUtc - startedOnUtc,
            completedOnUtc));

        return pipelineRun;
    }

    private Task RecordAuditAsync(
        Guid? pipelineRunId,
        Guid? routeId,
        Guid? sourceConnectionId,
        Guid? destinationId,
        Guid? mappingProfileId,
        string? resourceType,
        string action,
        string status,
        string message,
        int? resourceCount,
        string? triggeredBy,
        string? correlationId,
        CancellationToken cancellationToken)
    {
        return _auditService.RecordAsync(
            new RecordOperationalAuditLogRequest(
                pipelineRunId,
                routeId,
                sourceConnectionId,
                destinationId,
                mappingProfileId,
                resourceType,
                action,
                status,
                message,
                resourceCount,
                triggeredBy,
                correlationId),
            cancellationToken);
    }

    // Records a PHI-free lineage step (access/normalize/de-id) for the resource so the full chain can be queried.
    private Task RecordLineageAsync(
        Guid pipelineRunId,
        ResourcePipelineRoute route,
        DestinationConfiguration destination,
        MappingProfile mappingProfile,
        string resourceType,
        string? sourceResourceId,
        string action,
        CancellationToken cancellationToken)
    {
        return _lineageTracker.RecordAsync(
            new ResourceLineageRecord(
                pipelineRunId,
                route.Id,
                mappingProfile.SourceConnectionId,
                destination.Id,
                mappingProfile.Id,
                resourceType,
                sourceResourceId,
                action,
                "Completed",
                DateTime.UtcNow),
            cancellationToken);
    }

    /// <summary>
    /// Appends <c>_lastUpdated=gt{watermark}</c> to the search parameters for an incremental pull. The watermark is
    /// the start time of the most recent successfully completed run for this resource type, less a small overlap.
    /// Returns the original parameters unchanged when incremental sync is disabled, already pinned, or no prior run
    /// exists (first run is a full pull).
    /// </summary>
    private async Task<string?> ApplyIncrementalFilterAsync(
        string resourceType,
        string? searchParameters,
        CancellationToken cancellationToken)
    {
        if (!_incrementalSyncOptions.Enabled)
        {
            return searchParameters;
        }

        if (searchParameters is not null &&
            searchParameters.Contains("_lastUpdated", StringComparison.OrdinalIgnoreCase))
        {
            return searchParameters;
        }

        var recentRuns = await _pipelineRunRepository.GetRecentAsync(100, cancellationToken);
        var watermark = recentRuns
            .Where(run => string.Equals(run.Status, "Completed", StringComparison.OrdinalIgnoreCase))
            .Where(run => run.ResourceTypes.Any(type => string.Equals(type, resourceType, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(run => run.StartedOnUtc)
            .Select(run => (DateTime?)run.StartedOnUtc)
            .FirstOrDefault();

        if (watermark is null)
        {
            return searchParameters;
        }

        var since = watermark.Value.AddSeconds(-Math.Max(0, _incrementalSyncOptions.OverlapSeconds));
        var filter = $"_lastUpdated=gt{since.ToUniversalTime():yyyy-MM-ddTHH:mm:ss}Z";

        return string.IsNullOrWhiteSpace(searchParameters)
            ? filter
            : $"{searchParameters.TrimEnd('&', '?')}&{filter}";
    }

    /// <summary>
    /// Applies Expert Determination (k-anonymity) de-identification to the extracted cohort when configured. Returns
    /// the (generalized, possibly smaller) set; records an audit event when records are suppressed. Passthrough when
    /// no service is wired or the feature is disabled.
    /// </summary>
    private async Task<IReadOnlyList<ResourceEnvelope>> ApplyDataSetDeIdentificationAsync(
        Guid pipelineRunId,
        string resourceType,
        IReadOnlyList<ResourceEnvelope> resources,
        string? triggeredBy,
        string? correlationId,
        CancellationToken cancellationToken)
    {
        if (_dataSetDeIdentificationService is null || resources.Count == 0)
        {
            return resources;
        }

        var result = await _dataSetDeIdentificationService.DeIdentifyAsync(
            new DataSetDeIdentificationRequest(resourceType, resources.Select(r => r.RawJson).ToList()),
            cancellationToken);

        // Disabled passthrough: same count, no suppression, no generalization signal.
        if (result.KAnonymity == 0 && result.SuppressedCount == 0 && result.OutputCount == resources.Count)
        {
            return resources;
        }

        var deidentified = result.ResourcesJson
            .Select(json => EnvelopeFromDeIdentifiedJson(json, resourceType))
            .ToList();

        await RecordAuditAsync(
            pipelineRunId,
            null,
            null,
            null,
            null,
            resourceType,
            "DataSetDeIdentified",
            "Completed",
            $"Expert Determination (k={result.KAnonymity}) de-identification suppressed {result.SuppressedCount} of {result.InputCount} record(s).",
            deidentified.Count,
            triggeredBy,
            correlationId,
            cancellationToken);

        return deidentified;
    }

    private static ResourceEnvelope EnvelopeFromDeIdentifiedJson(string json, string resourceType)
    {
        string? id = null;
        if (JsonNode.Parse(json) is JsonObject resource)
        {
            id = resource["id"]?.GetValue<string>();
        }

        // Version/last-updated metadata is intentionally dropped from de-identified output.
        return new ResourceEnvelope(resourceType, id, json, null, null);
    }

    private RouteSourceKey CreateRouteSourceKey(ConfigurationSnapshot config, RouteMappingWorkItem route)
    {
        return new RouteSourceKey(
            route.MappingProfile.SourceConnectionId,
            route.SearchParameters);
    }

    private sealed record RouteSourceKey(
        Guid SourceConnectionId,
        string? SearchParameters);

    private sealed record RouteMappingWorkItem(
        ResourcePipelineRoute Route,
        MappingProfile MappingProfile,
        bool IsEnabled,
        int ExecutionOrder,
        string? SearchParameters);

    private sealed record RouteExecutionResult(int MappedCount, int WrittenCount);

    private sealed class NoOpLineageTracker : ILineageTracker
    {
        public Task RecordAsync(
            ResourceLineageRecord record,
            CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }

    private static bool IsScheduledPullMode(IngestionMode ingestionMode)
    {
        return ingestionMode is IngestionMode.ScheduledPull or IngestionMode.WebhookAndScheduledPull;
    }

    private static bool IsWebhookMode(IngestionMode ingestionMode)
    {
        return ingestionMode is IngestionMode.Webhook or IngestionMode.WebhookAndScheduledPull;
    }

    private static bool RouteDependenciesAreEnabled(ConfigurationSnapshot config, RouteMappingWorkItem route)
    {
        // A route's source and destination are owned by its mapping profile, so resolve them through the mapping.
        var mapping = route.MappingProfile;

        config.SourceConnectionsById.TryGetValue(mapping.SourceConnectionId, out var source);
        config.DestinationsById.TryGetValue(mapping.DestinationId, out var destination);
        WebhookConfiguration? webhook = null;
        if (route.Route.WebhookConfigurationId.HasValue)
        {
            config.WebhooksById.TryGetValue(route.Route.WebhookConfigurationId.Value, out webhook);
        }

        return source?.IsEnabled == true &&
               destination?.IsEnabled == true &&
               mapping.IsEnabled &&
               (!route.Route.WebhookConfigurationId.HasValue || webhook?.IsEnabled == true);
    }

    private static IReadOnlyList<RouteMappingWorkItem> ExpandRouteMappingWorkItems(ConfigurationSnapshot config)
    {
        var workItems = new List<RouteMappingWorkItem>();
        foreach (var route in config.Routes)
        {
            if (route.ResourceMappings.Count == 0)
            {
                if (config.MappingProfilesById.TryGetValue(route.MappingProfileId, out var mappingProfile))
                {
                    workItems.Add(new RouteMappingWorkItem(
                        route, mappingProfile, IsEnabled: true, ExecutionOrder: 0, route.SearchParameters));
                }

                continue;
            }

            foreach (var routeMapping in route.ResourceMappings.OrderBy(x => x.ExecutionOrder).ThenBy(x => x.MappingProfileId))
            {
                if (config.MappingProfilesById.TryGetValue(routeMapping.MappingProfileId, out var mappingProfile))
                {
                    workItems.Add(new RouteMappingWorkItem(
                        route,
                        mappingProfile,
                        routeMapping.IsEnabled,
                        routeMapping.ExecutionOrder,
                        // A per-mapping search parameter overrides the route's; null falls back to the route's value.
                        routeMapping.SearchParameters ?? route.SearchParameters));
                }
            }
        }

        return workItems;
    }

    // Resolves a route's resource type through its mapping profile (single source of truth).
    private static string? ResolveResourceType(ConfigurationSnapshot config, ResourcePipelineRoute route) =>
        config.MappingProfilesById.TryGetValue(route.MappingProfileId, out var mapping) ? mapping.ResourceType : null;

    // Resolves a route's source connection through its mapping profile (single source of truth).
    private static Guid? ResolveSourceConnectionId(ConfigurationSnapshot config, ResourcePipelineRoute route) =>
        config.MappingProfilesById.TryGetValue(route.MappingProfileId, out var mapping) ? mapping.SourceConnectionId : null;

    private IReadOnlyList<(string ResourceType, List<RouteMappingWorkItem> Routes)> ResolveRoutesByResourceType(
        ConfigurationSnapshot config,
        IReadOnlyCollection<string>? requestedResourceTypes)
    {
        var requested = requestedResourceTypes is null || requestedResourceTypes.Count == 0
            ? null
            : requestedResourceTypes
                .Select(SupportedFhirResourceTypes.Normalize)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Resource type is resolved from each route's mapping profile — the single source of truth for what to pull.
        var groups = ExpandRouteMappingWorkItems(config)
            .Where(x => !string.IsNullOrWhiteSpace(x.MappingProfile.ResourceType))
            .Where(x => requested is null || requested.Contains(x.MappingProfile.ResourceType))
            .GroupBy(x => x.MappingProfile.ResourceType, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => (ResourceType: group.Key, Routes: group.ToList()))
            .ToList();

        if (groups.Count == 0)
        {
            throw new InvalidOperationException("No resource routes were found for the requested pipeline run.");
        }

        return groups;
    }

    private async Task<FhirSourceConfiguration> BuildSourceConfigurationAsync(
        SourceConnection sourceConnection,
        string? searchParameters,
        CancellationToken cancellationToken)
    {
        var sourceType = sourceConnection.SourceSystemType switch
        {
            SourceSystemType.Sample => RuntimeSourceType.Sample,
            SourceSystemType.Epic => RuntimeSourceType.Epic,
            SourceSystemType.Cerner => RuntimeSourceType.Cerner,
            SourceSystemType.Allscripts => RuntimeSourceType.Allscripts,
            SourceSystemType.GenericFhir => RuntimeSourceType.GenericFhir,
            SourceSystemType.Athenahealth => RuntimeSourceType.GenericFhir,
            SourceSystemType.Healow => RuntimeSourceType.Healow,
            SourceSystemType.MeditechGreenfield => RuntimeSourceType.MeditechGreenfield,
            _ => throw new NotSupportedException($"Source system '{sourceConnection.SourceSystemType}' is not supported by the configured pipeline.")
        };

        string? privateKeyPem = null;
        if (sourceConnection.Authentication.PrivateKey is not null)
        {
            privateKeyPem = await _secretProvider.GetSecretAsync(
                sourceConnection.Authentication.PrivateKey,
                cancellationToken);
        }

        string? clientSecret = null;
        if (sourceConnection.Authentication.ClientSecret is not null)
        {
            clientSecret = await _secretProvider.GetSecretAsync(
                sourceConnection.Authentication.ClientSecret,
                cancellationToken);
        }

        return new FhirSourceConfiguration(
            sourceType,
            sourceConnection.Name,
            sourceConnection.BaseUrl,
            sourceConnection.Authentication.TokenEndpoint,
            sourceConnection.Authentication.ClientId,
            sourceConnection.Authentication.KeyId,
            privateKeyPem,
            sourceConnection.Authentication.Scopes,
            100,
            5,
            sourceConnection.Id,
            searchParameters,
            clientSecret,
            ApplicationType: sourceConnection.ApplicationType);
    }

    private async Task<IReadOnlyList<MappedDestinationRecord>> MapResourcesAsync(
        Guid pipelineRunId,
        Guid routeExecutionId,
        MappingProfile mappingProfile,
        IReadOnlyCollection<ResourceEnvelope> resources,
        List<string> errors,
        CancellationToken cancellationToken)
    {
        var mappedRecords = new List<MappedDestinationRecord>();
        var mappingFields = mappingProfile.Fields
            .Select(ConfigurationMapper.ToDto)
            .Where(field =>
                string.IsNullOrWhiteSpace(field.ResourceType) ||
                string.Equals(field.ResourceType, mappingProfile.ResourceType, StringComparison.OrdinalIgnoreCase))
            .Where(field =>
                string.IsNullOrWhiteSpace(field.DestinationObject) ||
                string.Equals(field.DestinationObject, mappingProfile.DestinationObject, StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (var resource in resources)
        {
            var result = _mappingEngine.Map(resource.RawJson, mappingFields);
            if (result.Errors.Count > 0)
            {
                errors.AddRange(result.Errors.Select(error =>
                    $"{mappingProfile.ResourceType}/{resource.ResourceId ?? "unknown"}: {error}"));
                continue;
            }

            var mappedRecord = new MappedDestinationRecord(
                pipelineRunId,
                mappingProfile.ResourceType,
                mappingProfile.DestinationObject,
                resource.ResourceId,
                result.Values,
                resource.RawJson);

            var normalizedMappedRecord = await _mappedRecordNormalizationService.NormalizeAsync(
                new MappedRecordNormalizationRequest(
                    resource.RawJson,
                    mappedRecord,
                    mappingFields),
                cancellationToken);
            mappedRecords.Add(normalizedMappedRecord);

            await _resourceHistoryRecorder.RecordMappedAsync(
                routeExecutionId,
                mappingProfile.ResourceType,
                resource.ResourceId,
                normalizedMappedRecord.Values,
                cancellationToken);
        }

        return mappedRecords;
    }

    // Loads the flat configuration once per run and indexes it for the in-memory joins the pipeline performs.
    private async Task<ConfigurationSnapshot> LoadConfigurationAsync(CancellationToken cancellationToken)
    {
        var sources = await _configurationRepository.GetSourceConnectionsAsync(cancellationToken);
        var destinations = await _configurationRepository.GetDestinationsAsync(cancellationToken);
        var mappings = await _configurationRepository.GetMappingProfilesAsync(cancellationToken);
        var routes = await _configurationRepository.GetRoutesAsync(cancellationToken);
        var webhooks = await _configurationRepository.GetWebhooksAsync(cancellationToken);

        return new ConfigurationSnapshot(sources, destinations, mappings, routes, webhooks);
    }

    private static T GetRequired<T>(IReadOnlyCollection<T> items, Guid id, string entityName)
        where T : FHIRBridge.SharedKernel.Abstractions.Entity<Guid>
    {
        return items.FirstOrDefault(x => x.Id == id)
            ?? throw new NotFoundException(entityName, id);
    }

    // In-memory index of the flat configuration for a single pipeline run.
    private sealed class ConfigurationSnapshot
    {
        public ConfigurationSnapshot(
            IReadOnlyList<SourceConnection> sourceConnections,
            IReadOnlyList<DestinationConfiguration> destinationConfigurations,
            IReadOnlyList<MappingProfile> mappingProfiles,
            IReadOnlyList<ResourcePipelineRoute> routes,
            IReadOnlyList<WebhookConfiguration> webhookConfigurations)
        {
            SourceConnections = sourceConnections;
            DestinationConfigurations = destinationConfigurations;
            MappingProfiles = mappingProfiles;
            Routes = routes;
            WebhookConfigurations = webhookConfigurations;

            SourceConnectionsById = sourceConnections.ToDictionary(x => x.Id);
            DestinationsById = destinationConfigurations.ToDictionary(x => x.Id);
            MappingProfilesById = mappingProfiles.ToDictionary(x => x.Id);
            WebhooksById = webhookConfigurations.ToDictionary(x => x.Id);
        }

        public IReadOnlyList<SourceConnection> SourceConnections { get; }
        public IReadOnlyList<DestinationConfiguration> DestinationConfigurations { get; }
        public IReadOnlyList<MappingProfile> MappingProfiles { get; }
        public IReadOnlyList<ResourcePipelineRoute> Routes { get; }
        public IReadOnlyList<WebhookConfiguration> WebhookConfigurations { get; }

        public IReadOnlyDictionary<Guid, SourceConnection> SourceConnectionsById { get; }
        public IReadOnlyDictionary<Guid, DestinationConfiguration> DestinationsById { get; }
        public IReadOnlyDictionary<Guid, MappingProfile> MappingProfilesById { get; }
        public IReadOnlyDictionary<Guid, WebhookConfiguration> WebhooksById { get; }
    }
}

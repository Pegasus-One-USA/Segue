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
using FHIRBridge.Domain.Aggregates;
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
    private readonly ITenantConfigurationRepository _tenantRepository;
    private readonly IFhirSourceClientFactory _sourceClientFactory;
    private readonly IJsonMappingEngine _mappingEngine;
    private readonly IConfiguredDestinationWriterFactory _destinationWriterFactory;
    private readonly ISecretProvider _secretProvider;
    private readonly IOperationalAuditService _auditService;
    private readonly IConfiguredPipelineRunRepository _pipelineRunRepository;
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
        ITenantConfigurationRepository tenantRepository,
        IFhirSourceClientFactory sourceClientFactory,
        IJsonMappingEngine mappingEngine,
        IConfiguredDestinationWriterFactory destinationWriterFactory,
        ISecretProvider secretProvider,
        IOperationalAuditService auditService,
        IConfiguredPipelineRunRepository pipelineRunRepository,
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
        _tenantRepository = tenantRepository;
        _sourceClientFactory = sourceClientFactory;
        _mappingEngine = mappingEngine;
        _destinationWriterFactory = destinationWriterFactory;
        _secretProvider = secretProvider;
        _auditService = auditService;
        _pipelineRunRepository = pipelineRunRepository;
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
        Guid tenantId,
        int count,
        CancellationToken cancellationToken)
    {
        return _pipelineRunRepository.GetRecentAsync(tenantId, count, cancellationToken);
    }

    public async Task SetRunEnabledAsync(
        Guid tenantId,
        Guid pipelineRunId,
        bool isEnabled,
        CancellationToken cancellationToken)
    {
        await _pipelineRunRepository.SetEnabledAsync(
            tenantId,
            pipelineRunId,
            isEnabled,
            cancellationToken);

        await RecordAuditAsync(
            tenantId,
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
        Guid tenantId,
        StartConfiguredPipelineRunRequest request,
        CancellationToken cancellationToken)
    {
        var startedOnUtc = DateTime.UtcNow;
        var pipelineRunId = Guid.NewGuid();
        var errors = new List<string>();
        var extractedCount = 0;
        var mappedCount = 0;
        var writtenCount = 0;

        var tenant = await _tenantRepository.GetByIdAsync(tenantId, cancellationToken)
            ?? throw new NotFoundException("Tenant", tenantId);
        var scheduledAtUtc = request.ScheduledAtUtc ?? DateTime.UtcNow;

        await RecordAuditAsync(
            tenantId,
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

        var routesByResourceType = ResolveRoutesByResourceType(tenant, request.ResourceTypes);
        var processedResourceTypes = new List<string>();

        foreach (var (resourceType, routesForType) in routesByResourceType)
        {
            processedResourceTypes.Add(resourceType);

            var enabledRoutes = routesForType
                .Where(route => route.IsEnabled)
                .Where(route => RouteDependenciesAreEnabled(tenant, route))
                .Where(route => IsScheduledPullMode(route.IngestionMode))
                .Where(route => request.RouteIds is not null
                    ? request.RouteIds.Contains(route.Id)
                    : !request.RunDueSchedulesOnly ||
                      ScheduleExpressionMatcher.IsDue(route.ScheduleExpression, scheduledAtUtc))
                .OrderBy(route => route.Priority)
                .ThenBy(route => route.Id)
                .ToList();

            if (enabledRoutes.Count == 0)
            {
                if (!request.RunDueSchedulesOnly)
                {
                    errors.Add($"{resourceType}: No enabled scheduled pipeline routes were found.");
                }

                continue;
            }

            foreach (var routeGroup in enabledRoutes.GroupBy(route => CreateRouteSourceKey(tenant, route)))
            {
                IReadOnlyList<ResourceEnvelope> resources;

                try
                {
                    var sourceConnection = GetRequired(
                        tenant.SourceConnections,
                        routeGroup.Key.SourceConnectionId,
                        "SourceConnection");

                    await RecordAuditAsync(
                        tenantId,
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
                            tenantId,
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
                        tenantId,
                        pipelineRunId,
                        resourceType,
                        resources,
                        request.TriggeredBy,
                        request.CorrelationId,
                        cancellationToken);

                    await RecordAuditAsync(
                        tenantId,
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
                        "Configured pipeline source extraction failed for tenant {TenantId}, resource {ResourceType}, source {SourceConnectionId}.",
                        tenantId,
                        resourceType,
                        routeGroup.Key.SourceConnectionId);

                    errors.Add($"{resourceType}/{routeGroup.Key.SourceConnectionId}: {exception.Message}");

                    await RecordAuditAsync(
                        tenantId,
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
                        tenant,
                        pipelineRunId,
                        resourceType,
                        route,
                        resources,
                        request.TriggeredBy,
                        request.CorrelationId,
                        errors,
                        cancellationToken);

                    mappedCount += result.MappedCount;
                    writtenCount += result.WrittenCount;
                }
            }
        }

        return await CompleteRunAsync(
            tenantId,
            pipelineRunId,
            processedResourceTypes,
            extractedCount,
            mappedCount,
            writtenCount,
            errors,
            startedOnUtc,
            request.TriggeredBy,
            request.CorrelationId,
            cancellationToken);
    }

    public async Task<ConfiguredPipelineRunDto> StartWebhookAsync(
        Guid tenantId,
        Guid webhookConfigurationId,
        WebhookIngestionRequest request,
        CancellationToken cancellationToken)
    {
        var startedOnUtc = DateTime.UtcNow;
        var pipelineRunId = Guid.NewGuid();
        var errors = new List<string>();
        var mappedCount = 0;
        var writtenCount = 0;

        var tenant = await _tenantRepository.GetByIdAsync(tenantId, cancellationToken)
            ?? throw new NotFoundException("Tenant", tenantId);
        var webhookConfiguration = GetRequired(
            tenant.WebhookConfigurations,
            webhookConfigurationId,
            "WebhookConfiguration");

        if (!webhookConfiguration.IsEnabled)
        {
            throw new InvalidOperationException("Webhook configuration is disabled.");
        }

        var webhookSourceConnection = GetRequired(
            tenant.SourceConnections,
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

        await RecordAuditAsync(
            tenantId,
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
            var webhookRoutes = tenant.ResourcePipelineRoutes
                .Where(route => string.Equals(tenant.ResolveResourceType(route), resourceType, StringComparison.OrdinalIgnoreCase))
                .Where(route => route.IsEnabled)
                .Where(route => RouteDependenciesAreEnabled(tenant, route))
                .Where(route => route.WebhookConfigurationId == webhookConfigurationId)
                .Where(route => IsWebhookMode(route.IngestionMode))
                .OrderBy(route => route.Priority)
                .ThenBy(route => route.Id)
                .ToList();

            if (webhookRoutes.Count == 0)
            {
                errors.Add($"{resourceType}: No enabled webhook pipeline routes were found.");
                continue;
            }

            foreach (var route in webhookRoutes)
            {
                var result = await ExecuteRouteAsync(
                    tenant,
                    pipelineRunId,
                    resourceType,
                    route,
                    resourcesForType.ToList(),
                    request.TriggeredBy,
                    request.CorrelationId,
                    errors,
                    cancellationToken);

                mappedCount += result.MappedCount;
                writtenCount += result.WrittenCount;
            }
        }

        return await CompleteRunAsync(
            tenantId,
            pipelineRunId,
            processedResourceTypes,
            extractedCount,
            mappedCount,
            writtenCount,
            errors,
            startedOnUtc,
            request.TriggeredBy,
            request.CorrelationId,
            cancellationToken);
    }

    private async Task<RouteExecutionResult> ExecuteRouteAsync(
        Tenant tenant,
        Guid pipelineRunId,
        string resourceType,
        ResourcePipelineRoute route,
        IReadOnlyCollection<ResourceEnvelope> resources,
        string? triggeredBy,
        string? correlationId,
        List<string> errors,
        CancellationToken cancellationToken)
    {
        // Source, destination, and resource type are all owned by the route's mapping profile.
        var mappingProfile = GetRequired(
            tenant.MappingProfiles,
            route.MappingProfileId,
            "MappingProfile");

        try
        {
            if (!RouteDependenciesAreEnabled(tenant, route))
            {
                await RecordAuditAsync(
                    tenant.Id,
                    pipelineRunId,
                    route.Id,
                    mappingProfile.SourceConnectionId,
                    mappingProfile.DestinationId,
                    route.MappingProfileId,
                    resourceType,
                    "RouteExecutionSkipped",
                    "Skipped",
                    "Resource pipeline route was skipped because one or more lifecycle dependencies are disabled.",
                    resources.Count,
                    triggeredBy,
                    correlationId,
                    cancellationToken);

                return new RouteExecutionResult(0, 0);
            }

            var destination = GetRequired(
                tenant.DestinationConfigurations,
                mappingProfile.DestinationId,
                "DestinationConfiguration");

            await RecordAuditAsync(
                tenant.Id,
                pipelineRunId,
                route.Id,
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
                tenant.Id,
                pipelineRunId,
                route,
                destination,
                mappingProfile,
                resourceType,
                resources,
                triggeredBy,
                correlationId,
                errors,
                cancellationToken);

            var mappedRecords = await MapResourcesAsync(
                tenant.Id,
                pipelineRunId,
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

            await RecordAuditAsync(
                tenant.Id,
                pipelineRunId,
                route.Id,
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

            return new RouteExecutionResult(mappedRecords.Count, writtenCount);
        }
        catch (Exception exception)
        {
            _logger.LogError(
                exception,
                "Configured pipeline route failed for tenant {TenantId}, resource {ResourceType}, route {RouteId}.",
                tenant.Id,
                resourceType,
                route.Id);

            errors.Add($"{resourceType}/route/{route.Id}: {exception.Message}");

            await RecordAuditAsync(
                tenant.Id,
                pipelineRunId,
                route.Id,
                mappingProfile.SourceConnectionId,
                mappingProfile.DestinationId,
                route.MappingProfileId,
                resourceType,
                "RouteExecutionFailed",
                "Failed",
                exception.Message,
                null,
                triggeredBy,
                correlationId,
                cancellationToken);

            return new RouteExecutionResult(0, 0);
        }
    }

    private async Task<IReadOnlyCollection<ResourceEnvelope>> PrepareResourcesForRouteAsync(
        Guid tenantId,
        Guid pipelineRunId,
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
            var governanceDecision = await _governancePolicyService.EvaluateAsync(
                new ResourceGovernanceContext(
                    tenantId,
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
                errors.Add($"{resourceType}/{resource.ResourceId ?? "unknown"}: Governance denied resource access. {governanceDecision.DenialReason}");

                await RecordAuditAsync(
                    tenantId,
                    pipelineRunId,
                    route.Id,
                    mappingProfile.SourceConnectionId,
                    destination.Id,
                    mappingProfile.Id,
                    resourceType,
                    "ResourceAccessDenied",
                    "Denied",
                    governanceDecision.DenialReason ?? "Governance policy denied resource access.",
                    1,
                    triggeredBy,
                    correlationId,
                    cancellationToken);

                continue;
            }

            var normalizationResult = await _normalizationService.NormalizeAsync(
                new ResourceNormalizationRequest(
                    tenantId,
                    pipelineRunId,
                    resourceType,
                    resource.ResourceId,
                    resource.RawJson),
                cancellationToken);
            var governedJson = normalizationResult.NormalizedJson;

            await RecordLineageAsync(
                tenantId, pipelineRunId, route, destination, mappingProfile, resourceType, resource.ResourceId,
                "ResourceNormalized", cancellationToken);

            if (governanceDecision.RequiresDeIdentification)
            {
                governedJson = await _deIdentificationService.DeIdentifyAsync(
                    new DeIdentificationRequest(
                        tenantId,
                        resourceType,
                        resource.ResourceId,
                        governedJson,
                        governanceDecision.AppliedPolicies),
                    cancellationToken);

                await RecordLineageAsync(
                    tenantId, pipelineRunId, route, destination, mappingProfile, resourceType, resource.ResourceId,
                    "ResourceDeIdentified", cancellationToken);
            }

            preparedResources.Add(resource with { RawJson = governedJson });

            await RecordLineageAsync(
                tenantId, pipelineRunId, route, destination, mappingProfile, resourceType, resource.ResourceId,
                "ResourceAccessed", cancellationToken);
        }

        return preparedResources;
    }

    private async Task<ConfiguredPipelineRunDto> CompleteRunAsync(
        Guid tenantId,
        Guid pipelineRunId,
        IReadOnlyList<string> resourceTypes,
        int extractedCount,
        int mappedCount,
        int writtenCount,
        IReadOnlyList<string> errors,
        DateTime startedOnUtc,
        string? triggeredBy,
        string? correlationId,
        CancellationToken cancellationToken)
    {
        var status = errors.Count == 0
            ? "Completed"
            : writtenCount > 0
                ? "CompletedWithErrors"
                : "Failed";

        await RecordAuditAsync(
            tenantId,
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
            tenantId,
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
            TriggerType: triggeredBy);

        await _pipelineRunRepository.AddAsync(pipelineRun, cancellationToken);

        _pipelineMetrics?.RecordRun(new PipelineRunMetric(
            tenantId,
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
        Guid tenantId,
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
                tenantId,
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
        Guid tenantId,
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
                tenantId,
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
        Guid tenantId,
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

        var recentRuns = await _pipelineRunRepository.GetRecentAsync(tenantId, 100, cancellationToken);
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
        Guid tenantId,
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
            new DataSetDeIdentificationRequest(tenantId, resourceType, resources.Select(r => r.RawJson).ToList()),
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
            tenantId,
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

    private static RouteSourceKey CreateRouteSourceKey(Tenant tenant, ResourcePipelineRoute route)
    {
        return new RouteSourceKey(
            tenant.ResolveSourceConnectionId(route) ?? Guid.Empty,
            route.SearchParameters);
    }

    private sealed record RouteSourceKey(
        Guid SourceConnectionId,
        string? SearchParameters);

    private static bool IsScheduledPullMode(IngestionMode ingestionMode)
    {
        return ingestionMode is IngestionMode.ScheduledPull or IngestionMode.WebhookAndScheduledPull;
    }

    private static bool IsWebhookMode(IngestionMode ingestionMode)
    {
        return ingestionMode is IngestionMode.Webhook or IngestionMode.WebhookAndScheduledPull;
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

    private IReadOnlyList<(string ResourceType, List<ResourcePipelineRoute> Routes)> ResolveRoutesByResourceType(
        Tenant tenant,
        IReadOnlyCollection<string>? requestedResourceTypes)
    {
        var requested = requestedResourceTypes is null || requestedResourceTypes.Count == 0
            ? null
            : requestedResourceTypes
                .Select(SupportedFhirResourceTypes.Normalize)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Resource type is resolved from each route's mapping profile — the single source of truth for what to pull.
        var groups = tenant.ResourcePipelineRoutes
            .Select(route => (Route: route, ResourceType: tenant.ResolveResourceType(route)))
            .Where(x => !string.IsNullOrWhiteSpace(x.ResourceType))
            .Where(x => requested is null || requested.Contains(x.ResourceType!))
            .GroupBy(x => x.ResourceType!, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => (ResourceType: group.Key, Routes: group.Select(x => x.Route).ToList()))
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
            sourceConnection.TenantId,
            sourceConnection.Id,
            searchParameters,
            clientSecret,
            ApplicationType: sourceConnection.ApplicationType);
    }

    private async Task<IReadOnlyList<MappedDestinationRecord>> MapResourcesAsync(
        Guid tenantId,
        Guid pipelineRunId,
        MappingProfile mappingProfile,
        IReadOnlyCollection<ResourceEnvelope> resources,
        List<string> errors,
        CancellationToken cancellationToken)
    {
        var mappedRecords = new List<MappedDestinationRecord>();
        var mappingFields = mappingProfile.Fields
            .Select(TenantConfigurationMapper.ToDto)
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
                tenantId,
                pipelineRunId,
                mappingProfile.ResourceType,
                mappingProfile.DestinationObject,
                resource.ResourceId,
                result.Values,
                resource.RawJson);

            mappedRecords.Add(await _mappedRecordNormalizationService.NormalizeAsync(
                new MappedRecordNormalizationRequest(
                    resource.RawJson,
                    mappedRecord,
                    mappingFields),
                cancellationToken));
        }

        return mappedRecords;
    }

    private static T GetRequired<T>(
        IEnumerable<T> values,
        Guid id,
        string entityName)
        where T : FHIRBridge.SharedKernel.Abstractions.Entity<Guid>
    {
        return values.FirstOrDefault(x => x.Id == id)
            ?? throw new NotFoundException(entityName, id);
    }

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
}

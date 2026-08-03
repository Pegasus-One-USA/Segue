using FHIRBridge.Application.Abstractions.Caching;
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
using FHIRBridge.Domain.ValueObjects;
using FHIRBridge.Governance;
using FHIRBridge.Infrastructure.Governance;
using FHIRBridge.Integration.Fhir;
using FHIRBridge.Runtime.Application.Abstractions.Connectors;
using FHIRBridge.Runtime.Application.DTOs;
using FHIRBridge.Runtime.Domain.Enums;
using FHIRBridge.Runtime.Domain.ValueObjects;
using FHIRBridge.SharedKernel.Exceptions;
using FHIRBridge.SharedKernel.Observability;
using Microsoft.Extensions.Logging;
using System.IO.Compression;
using System.Text.Json.Nodes;

namespace FHIRBridge.Infrastructure.Pipeline;

public sealed class ConfiguredPipelineService : IConfiguredPipelineService
{
    private readonly IConfigurationRepository _configurationRepository;
    private readonly IFhirSourceClientFactory _sourceClientFactory;
    private readonly IJsonMappingEngine _mappingEngine;
    private readonly IMappingMaterializer _mappingMaterializer;
    private readonly IConfiguredDestinationWriterFactory _destinationWriterFactory;
    private readonly ISecretProvider _secretProvider;
    private readonly IConfiguredPipelineRunRepository _pipelineRunRepository;
    private readonly IPipelineRunRouteExecutionRepository _routeExecutionRepository;
    private readonly IExecutionResourceHistoryRecorder _resourceHistoryRecorder;
    private readonly IResourceNormalizationService _normalizationService;
    private readonly IMappedRecordNormalizationService _mappedRecordNormalizationService;
    private readonly IGovernancePolicyService _governancePolicyService;
    private readonly IGovernanceLogger _governanceLogger;
    private readonly IDeIdentificationService _deIdentificationService;
    private readonly IDataSetDeIdentificationService? _dataSetDeIdentificationService;
    private readonly IFhirBulkExportClient? _bulkExportClient;
    private readonly IPipelineMetrics? _pipelineMetrics;
    private readonly IncrementalSyncOptions _incrementalSyncOptions;
    private readonly ISystemSettingsCache? _settingsCache;
    private readonly ILogger<ConfiguredPipelineService> _logger;
    private readonly IFailureDiagnosisClassifier _diagnosisClassifier;
    private readonly IDestinationSchemaService? _destinationSchemaService;
    private readonly IGlobalExceptionManager? _exceptionManager;

    public ConfiguredPipelineService(
        IConfigurationRepository configurationRepository,
        IFhirSourceClientFactory sourceClientFactory,
        IJsonMappingEngine mappingEngine,
        IMappingMaterializer mappingMaterializer,
        IConfiguredDestinationWriterFactory destinationWriterFactory,
        ISecretProvider secretProvider,
        IConfiguredPipelineRunRepository pipelineRunRepository,
        IPipelineRunRouteExecutionRepository routeExecutionRepository,
        IExecutionResourceHistoryRecorder resourceHistoryRecorder,
        ILogger<ConfiguredPipelineService> logger,
        IResourceNormalizationService? normalizationService = null,
        IMappedRecordNormalizationService? mappedRecordNormalizationService = null,
        IGovernancePolicyService? governancePolicyService = null,
        IDeIdentificationService? deIdentificationService = null,
        IFhirBulkExportClient? bulkExportClient = null,
        IPipelineMetrics? pipelineMetrics = null,
        IncrementalSyncOptions? incrementalSyncOptions = null,
        IDataSetDeIdentificationService? dataSetDeIdentificationService = null,
        IGovernanceLogger? governanceLogger = null,
        IFailureDiagnosisClassifier? diagnosisClassifier = null,
        ISystemSettingsCache? settingsCache = null,
        IDestinationSchemaService? destinationSchemaService = null,
        IGlobalExceptionManager? exceptionManager = null)
    {
        _configurationRepository = configurationRepository;
        _sourceClientFactory = sourceClientFactory;
        _mappingEngine = mappingEngine;
        _mappingMaterializer = mappingMaterializer;
        _destinationWriterFactory = destinationWriterFactory;
        _secretProvider = secretProvider;
        _pipelineRunRepository = pipelineRunRepository;
        _routeExecutionRepository = routeExecutionRepository;
        _resourceHistoryRecorder = resourceHistoryRecorder;
        _normalizationService = normalizationService ?? new PassThroughResourceNormalizationService();
        _mappedRecordNormalizationService = mappedRecordNormalizationService ?? new PassThroughMappedRecordNormalizationService();
        _governancePolicyService = governancePolicyService ?? new DefaultGovernancePolicyService();
        _governanceLogger = governanceLogger ?? new NullGovernanceLogger();
        _deIdentificationService = deIdentificationService ?? new PassThroughDeIdentificationService();
        _bulkExportClient = bulkExportClient;
        _pipelineMetrics = pipelineMetrics;
        _incrementalSyncOptions = incrementalSyncOptions ?? IncrementalSyncOptions.Default;
        _settingsCache = settingsCache;
        _dataSetDeIdentificationService = dataSetDeIdentificationService;
        _logger = logger;
        _diagnosisClassifier = diagnosisClassifier ?? new DefaultFailureDiagnosisClassifier();
        _destinationSchemaService = destinationSchemaService;
        _exceptionManager = exceptionManager;
    }

    // Persists a route/extraction failure into the shared ErrorLog store (via GlobalExceptionManager) so it
    // surfaces on both the Errors screen and Correlation Search, not just as a string in the run's `errors` list.
    private Task CaptureFailureAsync(
        Exception exception,
        Guid pipelineRunId,
        string? correlationId,
        CancellationToken cancellationToken)
    {
        if (_exceptionManager is null)
        {
            return Task.CompletedTask;
        }

        return _exceptionManager.CaptureAsync(
            exception,
            new ExceptionContext(
                Module: "Pipeline Run",
                CorrelationId: correlationId,
                ExecutionId: pipelineRunId.ToString()),
            cancellationToken);
    }

    /// <summary>
    /// Per docs/ERRORS_SCREEN_CATEGORIZATION_ANALYSIS.md §7-8: this pipeline's per-resource/per-route failure
    /// path previously passed <c>exception.Message</c> straight through — raw driver/HTTP text, uncategorized,
    /// unsanitized. This runs the same failure-diagnosis rules the Global Exception Manager uses (so a SQL login
    /// failure or an Epic invalid_client response gets the same plain-language cause either way) and sanitizes
    /// the technical detail before either reaches <c>errors</c>/<c>RecordFailedAsync</c>/the route's stored note.
    /// </summary>
    private string DescribeFailure(Exception exception)
    {
        var diagnosis = _diagnosisClassifier.Diagnose(exception, FHIRBridge.Governance.ErrorCategory.Unknown);
        var technicalDetail = FHIRBridge.Governance.SafeErrorText.SanitizeOr(
            exception.Message, "No further technical detail is available.");
        return $"{diagnosis.Cause} ({technicalDetail})";
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
        var inlineDownloads = new List<GeneratedFileDto>();
        var downloadUrls = new List<string>();

        var config = await LoadConfigurationAsync(cancellationToken);
        var scheduledAtUtc = request.ScheduledAtUtc ?? DateTime.UtcNow;

        // TriggerType is derived from the request shape rather than trusting a caller-supplied value, so it can
        // never drift from TriggeredBy the way it used to (see CompleteRunAsync's old TriggerType: triggeredBy bug).
        var triggerType = request.UseBulkExport
            ? "Bulk"
            : request.RunDueSchedulesOnly
                ? "Scheduled"
                : "Manual";

        // Only the synchronous, non-bulk-export API trigger (PipelineRunsController.Start) sets AllowInlineDownload —
        // schedules, dispatched/queued runs, and webhooks have no HTTP response to carry Download-mode bytes back
        // through, so a Download-mode CSV destination reached from those paths fails loudly instead of silently.
        var runStartedAtUtc = new DateTimeOffset(startedOnUtc, TimeSpan.Zero);

        var routesByResourceType = ResolveRoutesByResourceType(config, request.ResourceTypes);
        var processedResourceTypes = new List<string>();

        // Patient-compartment resource types (Observation, Condition, ServiceRequest, …) need a resolved patient id
        // to scope their search — normally supplied by an interactive SMART launch context, but Backend Services
        // has none (see FhirSourceConnectorBase.ApplyPatientScopeAsync). When the run also includes a "Patient"
        // route for the same source connection (processed first — see ResolveRoutesByResourceType's ordering),
        // the ids it resolves are cached here and threaded into every later resource type's search for that same
        // connection via FhirSourceConfiguration.PatientIds, mirroring the Runtime DAG's cohort-scoping
        // (SourceNodeExecutors' SearchCohortScopedAsync). No-op for connections that already have an interactive
        // patient context or an explicitly configured TargetPatientId/PatientIds.
        var patientIdsBySourceConnection = new Dictionary<Guid, List<string>>();

        // Pre-pass: for every source identity (RouteSourceKey) that will run bulk export this run, collect the full
        // set of resource types routed through it. A Group/System $export job scoped to a single resource type
        // (e.g. _type=Patient alone) trips Epic's Interconnect Group-export handling — it falls back to an
        // unscoped/demographics Patient search internally and returns "requires demographics or _id parameter"
        // (business-rule 59159). Requesting every routed type together in one job (_type=Patient,Observation,...),
        // exactly as the FHIR Bulk Data spec intends, avoids the single-type job entirely. This pass only inspects
        // config/enablement (no I/O), mirroring the same filters the main loop below applies per resource type.
        var bulkExportResourceTypesByKey = new Dictionary<RouteSourceKey, List<string>>();
        foreach (var (resourceType, routesForType) in routesByResourceType)
        {
            var enabledRoutesForBulkScan = FilterEnabledRoutes(routesForType, config, request, scheduledAtUtc);
            if (enabledRoutesForBulkScan.Count == 0)
            {
                continue;
            }

            foreach (var routeGroup in enabledRoutesForBulkScan.GroupBy(route => CreateRouteSourceKey(config, route)))
            {
                var sourceConnection = GetRequired(
                    config.SourceConnections,
                    routeGroup.Key.SourceConnectionId,
                    "SourceConnection");
                var workflowSourceConfiguration = routeGroup.Key.SourceConfigurationId is { } sourceConfigurationId
                    ? config.SourceConfigurationsById.GetValueOrDefault(sourceConfigurationId)
                    : null;
                var retrieval = workflowSourceConfiguration?.Retrieval ?? sourceConnection.Retrieval;
                var useBulkExport = request.UseBulkExport
                    || string.Equals(retrieval?.RetrievalMethod, "bulk-export", StringComparison.OrdinalIgnoreCase);

                if (!useBulkExport)
                {
                    continue;
                }

                if (!bulkExportResourceTypesByKey.TryGetValue(routeGroup.Key, out var types))
                {
                    types = [];
                    bulkExportResourceTypesByKey[routeGroup.Key] = types;
                }

                if (!types.Contains(resourceType, StringComparer.OrdinalIgnoreCase))
                {
                    types.Add(resourceType);
                }
            }
        }

        // One $export job per RouteSourceKey, shared across every resource type that key services — kicked off on
        // first use below and reused (not re-run) for every later resource type sharing the same key.
        var bulkExportResultsByKey = new Dictionary<RouteSourceKey, Task<IReadOnlyList<ResourceEnvelope>>>();

        foreach (var (resourceType, routesForType) in routesByResourceType)
        {
            processedResourceTypes.Add(resourceType);

            var enabledRoutes = FilterEnabledRoutes(routesForType, config, request, scheduledAtUtc);

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

                    // The workflow-specific configuration (retrieval settings, scopes) this mapping uses. Falls back
                    // to reading straight off the SourceConnection (today's pre-Slice-2b behavior) for any mapping
                    // profile that somehow still has no configuration — defensive, since AddMappingProfileAsync/
                    // UpdateMappingProfileAsync always provision one going forward and the Slice 1 migration
                    // backfilled every pre-existing profile.
                    var workflowSourceConfiguration = routeGroup.Key.SourceConfigurationId is { } sourceConfigurationId
                        ? config.SourceConfigurationsById.GetValueOrDefault(sourceConfigurationId)
                        : null;
                    var retrieval = workflowSourceConfiguration?.Retrieval ?? sourceConnection.Retrieval;
                    var scopes = workflowSourceConfiguration?.Scopes ?? sourceConnection.Authentication.Scopes;

                    // Bulk export runs either when the caller explicitly requests it (legacy API flag) or when the
                    // source connection itself is configured for it (RetrievalMethod == "bulk-export") — so a
                    // bulk-configured source uses $export from any trigger (manual, scheduled, or dispatched) without
                    // the caller having to know.
                    var useBulkExport = request.UseBulkExport
                        || string.Equals(retrieval?.RetrievalMethod, "bulk-export", StringComparison.OrdinalIgnoreCase);

                    // For the search path, make the pull incremental ("since last run") unless bulk export is used
                    // (bulk $export uses _since, not _lastUpdated) or the route already pins _lastUpdated.
                    var effectiveSearchParameters = useBulkExport
                        ? routeGroup.Key.SearchParameters
                        : await ApplyIncrementalFilterAsync(
                            resourceType,
                            routeGroup.Key.SearchParameters,
                            cancellationToken);

                    var cohortPatientIds = patientIdsBySourceConnection.TryGetValue(sourceConnection.Id, out var cachedCohort)
                        ? cachedCohort
                        : null;

                    var runtimeSourceConfiguration = await BuildSourceConfigurationAsync(
                        sourceConnection,
                        scopes,
                        effectiveSearchParameters,
                        cancellationToken,
                        cohortPatientIds);

                    if (useBulkExport)
                    {
                        // Bulk Data $export path: one job per RouteSourceKey covers every resource type routed
                        // through it this run (see bulkExportResourceTypesByKey above) — kicked off once and reused
                        // here for every resource type that shares the key, then filtered down to this type's rows.
                        if (_bulkExportClient is null)
                        {
                            throw new InvalidOperationException("Bulk export was requested but no bulk export client is configured.");
                        }

                        if (!bulkExportResultsByKey.TryGetValue(routeGroup.Key, out var bulkExportTask))
                        {
                            var batchedResourceTypes = bulkExportResourceTypesByKey.TryGetValue(routeGroup.Key, out var types)
                                ? types
                                : [resourceType];

                            bulkExportTask = _bulkExportClient.ExportAsync(
                                BuildBulkExportRequest(retrieval, batchedResourceTypes),
                                runtimeSourceConfiguration,
                                cancellationToken);
                            bulkExportResultsByKey[routeGroup.Key] = bulkExportTask;
                        }

                        var batchedResources = await bulkExportTask;
                        resources = batchedResources
                            .Where(resource => string.Equals(resource.ResourceType, resourceType, StringComparison.OrdinalIgnoreCase))
                            .ToList();
                    }
                    else
                    {
                        var sourceClient = _sourceClientFactory.Create(runtimeSourceConfiguration.SourceType);
                        resources = await sourceClient.SearchAsync(
                            resourceType,
                            runtimeSourceConfiguration,
                            cancellationToken);
                    }

                    extractedCount += resources.Count;

                    if (string.Equals(resourceType, "Patient", StringComparison.OrdinalIgnoreCase))
                    {
                        var resolvedIds = resources
                            .Select(r => r.ResourceId)
                            .Where(id => !string.IsNullOrWhiteSpace(id))
                            .Select(id => id!)
                            .ToList();

                        if (resolvedIds.Count > 0)
                        {
                            if (!patientIdsBySourceConnection.TryGetValue(sourceConnection.Id, out var existingIds))
                            {
                                existingIds = [];
                                patientIdsBySourceConnection[sourceConnection.Id] = existingIds;
                            }

                            existingIds.AddRange(resolvedIds.Except(existingIds));
                        }
                    }

                    // Expert Determination (k-anonymity) set-level de-identification across the extracted cohort.
                    // No-op passthrough unless explicitly enabled in configuration.
                    resources = await ApplyDataSetDeIdentificationAsync(
                        pipelineRunId,
                        resourceType,
                        resources,
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

                    errors.Add($"{resourceType}/{routeGroup.Key.SourceConnectionId}: {DescribeFailure(exception)}");

                    await CaptureFailureAsync(exception, pipelineRunId, request.CorrelationId, cancellationToken);

                    continue;
                }

                foreach (var route in routeGroup)
                {
                    var writeContext = new PipelineWriteContext(
                        request.AllowInlineDownload,
                        route.MappingProfile.Name,
                        runStartedAtUtc,
                        request.CorrelationId);

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
                        writeContext,
                        cancellationToken);

                    mappedCount += result.MappedCount;
                    writtenCount += result.WrittenCount;
                    if (result.InlineDownload is not null)
                    {
                        inlineDownloads.Add(result.InlineDownload);
                    }

                    if (result.DownloadUrl is not null)
                    {
                        downloadUrls.Add(result.DownloadUrl);
                    }
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
            CombineInlineDownloads(inlineDownloads),
            downloadUrls,
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
        var inlineDownloads = new List<GeneratedFileDto>();
        var downloadUrls = new List<string>();
        var runStartedAtUtc = new DateTimeOffset(startedOnUtc, TimeSpan.Zero);

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
                // Webhook-triggered runs never have an HTTP caller waiting to consume Download-mode bytes (the
                // caller here is the webhook sender, not a CSV consumer) — always false, never derived from a flag.
                var writeContext = new PipelineWriteContext(
                    AllowInlineDelivery: false,
                    route.MappingProfile.Name,
                    runStartedAtUtc,
                    request.CorrelationId);

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
                    writeContext,
                    cancellationToken);

                mappedCount += result.MappedCount;
                writtenCount += result.WrittenCount;
                if (result.InlineDownload is not null)
                {
                    inlineDownloads.Add(result.InlineDownload);
                }

                if (result.DownloadUrl is not null)
                {
                    downloadUrls.Add(result.DownloadUrl);
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
            CombineInlineDownloads(inlineDownloads),
            downloadUrls,
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
        PipelineWriteContext writeContext,
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
            var writeResult = await destinationWriter.WriteAsync(
                destination,
                mappingProfile,
                mappedRecords,
                writeContext,
                cancellationToken);
            var writtenCount = writeResult.Count;

            if (writeResult.RecordErrors is { Count: > 0 } writeRecordErrors)
            {
                errors.AddRange(writeRecordErrors);
            }

            if (writtenCount > 0)
            {
                // A writer that isolates per-record failures (see RecordErrors above) reports exactly which
                // records landed via WrittenResourceIds — record history against that, not the whole attempted
                // batch, so "stored" reflects reality even when some records in the batch failed. A writer that
                // doesn't support per-record isolation leaves WrittenResourceIds null; Count then really does mean
                // "the whole batch succeeded" (a partial write there throws and fails the whole route instead), so
                // falling back to the full mappedRecords list is still correct.
                await _resourceHistoryRecorder.RecordStoredAsync(
                    routeExecutionId,
                    writeResult.WrittenResourceIds ?? mappedRecords.Select(record => record.SourceResourceId).ToList(),
                    cancellationToken);
            }

            await _governanceLogger.LogExportAsync(
                new ExportEntry(
                    destination.Name,
                    destination.DestinationType.ToString(),
                    writtenCount,
                    writtenCount > 0 ? "Succeeded" : "NoData",
                    writeResult.InlineDownload?.Content.Length,
                    pipelineRunId,
                    correlationId),
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

            return new RouteExecutionResult(
                mappedRecords.Count,
                writtenCount,
                writeResult.InlineDownload is { } inlineFile
                    ? new GeneratedFileDto(inlineFile.FileName, inlineFile.ContentType, inlineFile.Content)
                    : null,
                writeResult.DownloadUrl);
        }
        catch (Exception exception)
        {
            _logger.LogError(
                exception,
                "Configured pipeline route failed for resource {ResourceType}, route {RouteId}.",
                resourceType,
                route.Route.Id);

            var failureDescription = DescribeFailure(exception);
            errors.Add($"{resourceType}/route/{route.Route.Id}: {failureDescription}");

            await CaptureFailureAsync(exception, pipelineRunId, correlationId, cancellationToken);

            await _routeExecutionRepository.CompleteAsync(
                routeExecutionId,
                PipelineRunRouteExecutionStatus.Failed,
                resources.Count,
                0,
                0,
                failureDescription,
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

            // HIPAA §164.312(b) data-access evidence: every access decision — allowed or denied — is
            // recorded PHI-free (identifiers only), regardless of what happens to the resource afterward.
            var purpose = $"Pipeline export via mapping '{mappingProfile.Name}' (route {route.Id:N}, triggered by {triggeredBy ?? "manual"})";
            await _governanceLogger.LogDataAccessAsync(
                new DataAccessEntry(
                    resourceType,
                    resource.ResourceId,
                    governanceDecision.IsAllowed ? "Allowed" : "Denied",
                    PatientId: resourceType == "Patient" ? resource.ResourceId : null,
                    Purpose: purpose,
                    PipelineRunId: pipelineRunId,
                    CorrelationId: correlationId),
                cancellationToken);

            if (!governanceDecision.IsAllowed)
            {
                var denialReason = governanceDecision.DenialReason ?? "Governance policy denied resource access.";
                errors.Add($"{resourceType}/{resource.ResourceId ?? "unknown"}: Governance denied resource access. {governanceDecision.DenialReason}");

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

            if (normalizationResult.Warnings.Count > 0)
            {
                await _governanceLogger.LogValidationFailureAsync(
                    new ValidationFailureEntry(
                        resourceType,
                        resource.ResourceId,
                        normalizationResult.Warnings,
                        normalizationResult.DataQualityScore,
                        pipelineRunId,
                        correlationId),
                    cancellationToken);
            }

            if (governanceDecision.RequiresDeIdentification)
            {
                governedJson = await _deIdentificationService.DeIdentifyAsync(
                    new DeIdentificationRequest(
                        resourceType,
                        resource.ResourceId,
                        governedJson,
                        governanceDecision.AppliedPolicies),
                    cancellationToken);
            }

            preparedResources.Add(resource with { RawJson = governedJson });
        }

        return preparedResources;
    }

    /// <summary>
    /// A run normally produces at most one Download-mode file (one destination reached via a manual "Run Now").
    /// If more than one route in the same run happened to write one, they're zipped together rather than silently
    /// dropping all but the first.
    /// </summary>
    private static GeneratedFileDto? CombineInlineDownloads(IReadOnlyList<GeneratedFileDto> files)
    {
        if (files.Count == 0)
        {
            return null;
        }

        if (files.Count == 1)
        {
            return files[0];
        }

        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var file in files)
            {
                var entry = archive.CreateEntry(file.FileName);
                using var entryStream = entry.Open();
                entryStream.Write(file.Content);
            }
        }

        return new GeneratedFileDto("download.zip", "application/zip", buffer.ToArray());
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
        GeneratedFileDto? inlineDownload,
        IReadOnlyList<string> downloadUrls,
        CancellationToken cancellationToken)
    {
        var status = errors.Count == 0
            ? "Completed"
            : writtenCount > 0
                ? "CompletedWithErrors"
                : "Failed";

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
            TriggerType: triggerType,
            InlineDownload: inlineDownload,
            DownloadUrls: downloadUrls.Count == 0 ? null : downloadUrls,
            CorrelationId: correlationId);

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
        var incrementalSyncEnabled = _settingsCache is null
            ? _incrementalSyncOptions.Enabled
            : await _settingsCache.GetBoolAsync("IncrementalSync:Enabled", _incrementalSyncOptions.Enabled, cancellationToken);

        if (!incrementalSyncEnabled)
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

        var overlapSeconds = _settingsCache is null
            ? _incrementalSyncOptions.OverlapSeconds
            : await _settingsCache.GetIntAsync("IncrementalSync:OverlapSeconds", _incrementalSyncOptions.OverlapSeconds, cancellationToken);
        var since = watermark.Value.AddSeconds(-Math.Max(0, overlapSeconds));
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
            route.MappingProfile.SourceConfigurationId,
            route.SearchParameters);
    }

    private sealed record RouteSourceKey(
        Guid SourceConnectionId,
        Guid? SourceConfigurationId,
        string? SearchParameters);

    private sealed record RouteMappingWorkItem(
        ResourcePipelineRoute Route,
        MappingProfile MappingProfile,
        bool IsEnabled,
        int ExecutionOrder,
        string? SearchParameters);

    private sealed record RouteExecutionResult(
        int MappedCount,
        int WrittenCount,
        GeneratedFileDto? InlineDownload = null,
        string? DownloadUrl = null);

    private static bool IsScheduledPullMode(IngestionMode ingestionMode)
    {
        return ingestionMode is IngestionMode.ScheduledPull or IngestionMode.WebhookAndScheduledPull;
    }

    private static bool IsWebhookMode(IngestionMode ingestionMode)
    {
        return ingestionMode is IngestionMode.Webhook or IngestionMode.WebhookAndScheduledPull;
    }

    private static List<RouteMappingWorkItem> FilterEnabledRoutes(
        IEnumerable<RouteMappingWorkItem> routesForType,
        ConfigurationSnapshot config,
        StartConfiguredPipelineRunRequest request,
        DateTime scheduledAtUtc)
    {
        return routesForType
            .Where(route => route.Route.IsEnabled)
            .Where(route => route.IsEnabled)
            .Where(route => RouteDependenciesAreEnabled(config, route))
            .Where(route => IsScheduledPullMode(route.Route.IngestionMode))
            .Where(route => request.RouteIds is not null
                ? request.RouteIds.Contains(route.Route.Id)
                : !request.RunDueSchedulesOnly ||
                  ScheduleExpressionMatcher.IsDue(route.Route.ScheduleExpression, scheduledAtUtc, route.Route.TimeZoneId))
            .OrderBy(route => route.Route.Priority)
            .ThenBy(route => route.Route.Id)
            .ThenBy(route => route.ExecutionOrder)
            .ToList();
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
        // "Patient" is ordered first (ties otherwise broken alphabetically) so any patient cohort it resolves is
        // already cached in patientIdsBySourceConnection by the time other patient-compartment resource types in
        // this same run reach BuildSourceConfigurationAsync.
        var groups = ExpandRouteMappingWorkItems(config)
            .Where(x => !string.IsNullOrWhiteSpace(x.MappingProfile.ResourceType))
            .Where(x => requested is null || requested.Contains(x.MappingProfile.ResourceType))
            .GroupBy(x => x.MappingProfile.ResourceType, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => string.Equals(group.Key, "Patient", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => (ResourceType: group.Key, Routes: group.ToList()))
            .ToList();

        if (groups.Count == 0)
        {
            throw new InvalidOperationException("No resource routes were found for the requested pipeline run.");
        }

        return groups;
    }

    // Projects the persisted bulk-export retrieval settings onto a runtime $export request for one resource type.
    // Scope defaults to System when unset (the historical behavior); group/patient carry their id list. The _since
    // cursor is applied only when incremental sync is enabled and a prior successful run recorded a timestamp.
    internal static FhirBulkExportRequest BuildBulkExportRequest(SourceRetrievalConfiguration? retrieval, string resourceType)
        => BuildBulkExportRequest(retrieval, (IReadOnlyList<string>)[resourceType]);

    // Same projection, batched across every resource type a single $export job should cover (see
    // bulkExportResourceTypesByKey in StartAsync) — a Group/System job scoped to just one resource type (e.g.
    // _type=Patient alone) can trip an Epic Interconnect business rule that requires demographics/_id for a bare
    // Patient search, since Epic resolves Group membership via an internal Patient search for that type.
    internal static FhirBulkExportRequest BuildBulkExportRequest(SourceRetrievalConfiguration? retrieval, IReadOnlyList<string> resourceTypes)
    {
        var scope = MapBulkExportScope(retrieval?.ExportScope);

        DateTimeOffset? since = retrieval is { IncrementalSyncEnabled: true, LastSuccessfulSyncUtc: { } syncedAt }
            ? new DateTimeOffset(DateTime.SpecifyKind(syncedAt, DateTimeKind.Utc))
            : null;

        return new FhirBulkExportRequest(
            scope,
            GroupId: scope == BulkExportScope.Group ? retrieval?.GroupId : null,
            ResourceTypes: BulkExportScopes.ResolveTypeParameter(scope, resourceTypes),
            Since: since,
            PatientIds: scope == BulkExportScope.Patient ? retrieval?.PatientIds : null,
            OutputFormat: retrieval?.OutputFormat);
    }

    // Maps the persisted wizard scope token to the runtime enum via the shared parser (not a switch on
    // ApplicationType), so both pipeline planes agree and it stays clear of the no-switch architecture rule.
    internal static BulkExportScope MapBulkExportScope(string? exportScope) => BulkExportScopes.Parse(exportScope);

    private async Task<FhirSourceConfiguration> BuildSourceConfigurationAsync(
        SourceConnection sourceConnection,
        IReadOnlyCollection<string> scopes,
        string? searchParameters,
        CancellationToken cancellationToken,
        IReadOnlyCollection<string>? patientIds = null)
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

        // A loopback base URL has no real OAuth server — skip resolving credential secrets and clear ApplicationType
        // so CompositeFhirAccessTokenProvider's legacy inference returns an empty token (no Authorization header)
        // instead of routing into a real JWT/client-credentials exchange. Mirrors SourceConnectionRuntimeResolver;
        // real (non-loopback) sources are completely unaffected.
        var isLoopback = Uri.TryCreate(sourceConnection.BaseUrl, UriKind.Absolute, out var baseUri) && baseUri.IsLoopback;

        string? privateKeyPem = null;
        if (!isLoopback && sourceConnection.Authentication.PrivateKey is not null)
        {
            privateKeyPem = await _secretProvider.GetSecretAsync(
                sourceConnection.Authentication.PrivateKey,
                cancellationToken);
        }

        string? clientSecret = null;
        if (!isLoopback && sourceConnection.Authentication.ClientSecret is not null)
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
            scopes,
            100,
            5,
            sourceConnection.Id,
            searchParameters,
            clientSecret,
            ApplicationType: isLoopback ? null : sourceConnection.ApplicationType,
            PatientIds: patientIds);
    }

    /// <summary>
    /// Fills each field's <see cref="MappingFieldDto.MaxLength"/>/<see cref="MappingFieldDto.Precision"/>/
    /// <see cref="MappingFieldDto.Scale"/> from the destination's live column metadata (the same introspection the
    /// mapping-editor column picker already reads) so <see cref="JsonMappingEngine"/> can reject an
    /// oversized/overflowing value before it's ever sent to the destination, instead of only finding out from a
    /// truncation/overflow SqlException at write time. Fetched once per route execution, not per resource. Falls
    /// back to the fields unchanged (no length/precision enforcement) when no schema service is wired up, the
    /// destination isn't relational, or its schema can't be read right now — this is a belt-and-suspenders
    /// improvement, not something that should itself fail a run.
    /// </summary>
    private async Task<List<MappingFieldDto>> EnrichWithDestinationSchemaAsync(
        List<MappingFieldDto> mappingFields,
        MappingProfile mappingProfile,
        CancellationToken cancellationToken)
    {
        if (_destinationSchemaService is null)
        {
            return mappingFields;
        }

        DestinationSchemaDto schema;
        try
        {
            schema = await _destinationSchemaService.GetSchemaAsync(mappingProfile.DestinationId, cancellationToken);
        }
        catch (Exception)
        {
            return mappingFields;
        }

        var tableName = DestinationObjectParser.ParseTableName(mappingProfile.DestinationObject);
        var table = schema.Tables.FirstOrDefault(t =>
            string.Equals(t.FullName, tableName, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(t.TableName, tableName, StringComparison.OrdinalIgnoreCase));

        if (table is null)
        {
            return mappingFields;
        }

        var columnsByName = table.Columns.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);

        return mappingFields
            .Select(field => columnsByName.TryGetValue(field.TargetField, out var column)
                ? field with { MaxLength = column.MaxLength, Precision = column.NumericPrecision, Scale = column.NumericScale }
                : field)
            .ToList();
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
        // Note: deliberately NOT filtered by DestinationObject — a profile's Fields span the root table plus
        // any child tables (ArrayPolicy.SeparateDestination), and the engine (via MappingFieldDto.DestinationObject)
        // is what routes each field to the right table. Filtering here would starve child tables of their fields.
        var mappingFields = mappingProfile.Fields
            .Select(ConfigurationMapper.ToDto)
            .Where(field =>
                string.IsNullOrWhiteSpace(field.ResourceType) ||
                string.Equals(field.ResourceType, mappingProfile.ResourceType, StringComparison.OrdinalIgnoreCase))
            .Where(field => field.IsEnabled)
            .ToList();

        mappingFields = await EnrichWithDestinationSchemaAsync(mappingFields, mappingProfile, cancellationToken);

        foreach (var resource in resources)
        {
            var result = _mappingEngine.Map(resource.RawJson, mappingFields);
            if (result.Errors.Count > 0)
            {
                errors.AddRange(result.Errors.Select(error =>
                    $"{mappingProfile.ResourceType}/{resource.ResourceId ?? "unknown"}: {error}"));
                continue;
            }

            var dataset = _mappingMaterializer.Materialize(mappingProfile.DestinationObject, result);
            var childTables = BuildChildTableRecords(dataset, mappingFields, mappingProfile.ResourceType);
            var referenceLookups = result.ReferenceLookups is { Count: > 0 }
                ? result.ReferenceLookups
                    .Select(l => new MappedReferenceLookup(l.TargetField, l.LookupTable, l.LookupKeyColumn, l.ReferenceId))
                    .ToArray()
                : null;

            foreach (var parentRow in dataset.ParentRows)
            {
                var mappedRecord = new MappedDestinationRecord(
                    pipelineRunId,
                    mappingProfile.ResourceType,
                    mappingProfile.DestinationObject,
                    resource.ResourceId,
                    parentRow,
                    resource.RawJson,
                    childTables,
                    referenceLookups);

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
        }

        return mappedRecords;
    }

    /// <summary>
    /// Converts the materializer's <see cref="MappingChildTableDto"/> output into writer-ready
    /// <see cref="MappedChildTableRecord"/>s, resolving each child table's FK/parent-key column names from the
    /// (already import-populated) <see cref="MappingFieldDto.ForeignKeyColumn"/>/<see cref="MappingFieldDto.ParentKeyColumn"/>
    /// metadata on one of its own fields. A child table with no field carrying that metadata can't be linked back
    /// to a parent row — skipped with a warning rather than attempted with a garbage/missing FK value.
    /// </summary>
    private IReadOnlyList<MappedChildTableRecord>? BuildChildTableRecords(
        MaterializedDataset dataset, IReadOnlyList<MappingFieldDto> mappingFields, string resourceType)
    {
        if (dataset.ChildTables.Count == 0)
        {
            return null;
        }

        var records = new List<MappedChildTableRecord>();
        foreach (var childTable in dataset.ChildTables)
        {
            var fkField = mappingFields.FirstOrDefault(f =>
                string.Equals(f.DestinationObject, childTable.Name, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(f.ForeignKeyColumn));

            if (fkField is null)
            {
                _logger.LogWarning(
                    "{ResourceType}: child table '{ChildTable}' produced {RowCount} row(s) but no mapped field " +
                    "carries ForeignKeyColumn metadata for it — its rows cannot be linked to a parent and were not written.",
                    resourceType, childTable.Name, childTable.Rows.Count);
                continue;
            }

            records.Add(new MappedChildTableRecord(
                childTable.Name,
                fkField.ForeignKeyColumn!,
                fkField.ParentKeyColumn ?? "Id",
                childTable.Rows));
        }

        return records.Count > 0 ? records : null;
    }

    // Loads the flat configuration once per run and indexes it for the in-memory joins the pipeline performs.
    private async Task<ConfigurationSnapshot> LoadConfigurationAsync(CancellationToken cancellationToken)
    {
        var sources = await _configurationRepository.GetSourceConnectionsAsync(cancellationToken);
        var sourceConfigurations = await _configurationRepository.GetSourceConfigurationsAsync(cancellationToken);
        var destinations = await _configurationRepository.GetDestinationsAsync(cancellationToken);
        var mappings = await _configurationRepository.GetMappingProfilesAsync(cancellationToken);
        var routes = await _configurationRepository.GetRoutesAsync(cancellationToken);
        var webhooks = await _configurationRepository.GetWebhooksAsync(cancellationToken);

        return new ConfigurationSnapshot(sources, sourceConfigurations, destinations, mappings, routes, webhooks);
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
            IReadOnlyList<SourceConfiguration> sourceConfigurations,
            IReadOnlyList<DestinationConfiguration> destinationConfigurations,
            IReadOnlyList<MappingProfile> mappingProfiles,
            IReadOnlyList<ResourcePipelineRoute> routes,
            IReadOnlyList<WebhookConfiguration> webhookConfigurations)
        {
            SourceConnections = sourceConnections;
            SourceConfigurations = sourceConfigurations;
            DestinationConfigurations = destinationConfigurations;
            MappingProfiles = mappingProfiles;
            Routes = routes;
            WebhookConfigurations = webhookConfigurations;

            SourceConnectionsById = sourceConnections.ToDictionary(x => x.Id);
            SourceConfigurationsById = sourceConfigurations.ToDictionary(x => x.Id);
            DestinationsById = destinationConfigurations.ToDictionary(x => x.Id);
            MappingProfilesById = mappingProfiles.ToDictionary(x => x.Id);
            WebhooksById = webhookConfigurations.ToDictionary(x => x.Id);
        }

        public IReadOnlyList<SourceConnection> SourceConnections { get; }
        public IReadOnlyList<SourceConfiguration> SourceConfigurations { get; }
        public IReadOnlyList<DestinationConfiguration> DestinationConfigurations { get; }
        public IReadOnlyList<MappingProfile> MappingProfiles { get; }
        public IReadOnlyList<ResourcePipelineRoute> Routes { get; }
        public IReadOnlyList<WebhookConfiguration> WebhookConfigurations { get; }

        public IReadOnlyDictionary<Guid, SourceConnection> SourceConnectionsById { get; }
        public IReadOnlyDictionary<Guid, SourceConfiguration> SourceConfigurationsById { get; }
        public IReadOnlyDictionary<Guid, DestinationConfiguration> DestinationsById { get; }
        public IReadOnlyDictionary<Guid, MappingProfile> MappingProfilesById { get; }
        public IReadOnlyDictionary<Guid, WebhookConfiguration> WebhooksById { get; }
    }
}

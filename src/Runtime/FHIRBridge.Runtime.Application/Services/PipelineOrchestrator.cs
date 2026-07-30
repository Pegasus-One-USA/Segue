using FHIRBridge.Runtime.Application.Abstractions.Connectors;
using FHIRBridge.Runtime.Application.Abstractions.Destinations;
using FHIRBridge.Runtime.Application.Abstractions.Persistence;
using FHIRBridge.Runtime.Application.Abstractions.Pipeline;
using FHIRBridge.Runtime.Application.Abstractions.Transformations;
using FHIRBridge.Runtime.Application.DTOs;
using FHIRBridge.Runtime.Application.Pipeline;
using FHIRBridge.Runtime.Application.Mappings;
using FHIRBridge.Runtime.Domain.Entities;
using FHIRBridge.Runtime.Domain.Enums;
using FHIRBridge.Runtime.Domain.Fhir;
using FHIRBridge.Runtime.Domain.ValueObjects;
using FHIRBridge.Observability;
using FHIRBridge.Governance;
using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace FHIRBridge.Runtime.Application.Services;

public sealed class PipelineOrchestrator : IPipelineOrchestrator
{
    private const int MaxExtractionParallelism = 4;

    private readonly IFhirSourceClientFactory _sourceClientFactory;
    private readonly IDestinationWriterFactory _destinationWriterFactory;
    private readonly IResourceTransformer _resourceTransformer;
    private readonly IPipelineRunStore _pipelineRunStore;
    private readonly IFhirBulkExportClient _bulkExportClient;
    private readonly ILogger<PipelineOrchestrator> _logger;
    private readonly IGlobalExceptionManager? _exceptionManager;

    public PipelineOrchestrator(
        IFhirSourceClientFactory sourceClientFactory,
        IDestinationWriterFactory destinationWriterFactory,
        IResourceTransformer resourceTransformer,
        IPipelineRunStore pipelineRunStore,
        IFhirBulkExportClient bulkExportClient,
        ILogger<PipelineOrchestrator> logger,
        IGlobalExceptionManager? exceptionManager = null)
    {
        _sourceClientFactory = sourceClientFactory;
        _destinationWriterFactory = destinationWriterFactory;
        _resourceTransformer = resourceTransformer;
        _pipelineRunStore = pipelineRunStore;
        _bulkExportClient = bulkExportClient;
        _logger = logger;
        _exceptionManager = exceptionManager;
    }

    public async Task<PipelineRunDto> StartAsync(
        StartPipelineRunRequest request,
        CancellationToken cancellationToken)
    {
        using var activity = FhirBridgeActivitySource.Instance.StartActivity("PipelineRun.Process", ActivityKind.Consumer);
        activity?.SetTag("correlation_id", request.CorrelationId);

        var resourceTypes = request.ResourceTypes
            .Select(SupportedFhirResourceTypes.Normalize)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var pipelineRun = new PipelineRun(
            request.Source.SourceType,
            request.Destination.DestinationType,
            resourceTypes,
            request.TriggeredBy,
            request.CorrelationId);

        await _pipelineRunStore.AddAsync(pipelineRun, cancellationToken);
        await AddEventAsync(
            pipelineRun,
            "PipelineStarted",
            null,
            null,
            null,
            $"Pipeline run started for {resourceTypes.Length} supported FHIR resource type(s).",
            cancellationToken);

        try
        {
            var sourceClient = _sourceClientFactory.Create(request.Source.SourceType);
            var destinationWriter = _destinationWriterFactory.Create(request.Destination.DestinationType);
            var transformedResources = new List<ResourceEnvelope>();

            // The execution order is computed from an explicit DAG rather than hard-coded, so the per-resource stages
            // always run dependency-first (Extraction → Governance → Transform), with Output as the terminal fan-in.
            var graph = PipelineGraphFactory.CreateDefault();
            _logger.LogInformation(
                "Pipeline run {PipelineRunId} stage plan: {StagePlan}.",
                pipelineRun.Id,
                string.Join(" -> ", graph.ExecutionOrder));

            // Fan-out: extract every resource type concurrently (bounded). Extraction is read-only I/O against the
            // source, so it is the safe stage to parallelize; the slow EHR round-trips overlap instead of serializing.
            var extractionResults = await ParallelFanOut.RunAsync(
                resourceTypes,
                MaxExtractionParallelism,
                (resourceType, ct) => sourceClient.SearchAsync(resourceType, request.Source, ct),
                cancellationToken);

            // Fan-in: record extraction bookkeeping and run the stateful stages on a single thread in DAG order, so
            // run-state mutation (steps, counters, events) stays deterministic and thread-safe.
            var perResourceStages = graph.ExecutionOrder
                .Where(stage => !string.Equals(stage, PipelineGraphFactory.Output, StringComparison.OrdinalIgnoreCase))
                .ToList();

            for (var i = 0; i < resourceTypes.Length; i++)
            {
                var resourceType = resourceTypes[i];
                IReadOnlyList<ResourceEnvelope> extractedResources = extractionResults[i];

                foreach (var stage in perResourceStages)
                {
                    switch (stage)
                    {
                        case PipelineGraphFactory.Extraction:
                            await RecordExtractionAsync(pipelineRun, resourceType, extractedResources, cancellationToken);
                            break;
                        case PipelineGraphFactory.Governance:
                            await GovernAsync(pipelineRun, resourceType, extractedResources, cancellationToken);
                            break;
                        case PipelineGraphFactory.Transform:
                            transformedResources.AddRange(await TransformAsync(
                                pipelineRun, resourceType, extractedResources, cancellationToken));
                            break;
                    }
                }
            }

            await WriteOutputAsync(
                pipelineRun,
                destinationWriter,
                request.Destination,
                transformedResources,
                cancellationToken);

            pipelineRun.Complete();

            await AddEventAsync(
                pipelineRun,
                "PipelineCompleted",
                null,
                null,
                null,
                $"Pipeline run completed. Extracted {pipelineRun.ExtractedResourceCount}; wrote {pipelineRun.WrittenResourceCount}.",
                cancellationToken);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Runtime pipeline run {PipelineRunId} failed.", pipelineRun.Id);
            pipelineRun.Fail(exception.Message);

            await CaptureFailureAsync(pipelineRun, exception, cancellationToken);

            await AddEventAsync(
                pipelineRun,
                "PipelineFailed",
                null,
                null,
                null,
                "Pipeline run failed. See failure message on the run record.",
                cancellationToken);
        }

        await _pipelineRunStore.UpdateAsync(pipelineRun, cancellationToken);

        return PipelineDtoMapper.ToDto(pipelineRun);
    }

    public async Task<PipelineRunDto> StartBulkExportAsync(
        StartBulkExportRunRequest request,
        CancellationToken cancellationToken)
    {
        using var activity = FhirBridgeActivitySource.Instance.StartActivity("PipelineRun.Process", ActivityKind.Consumer);
        activity?.SetTag("correlation_id", request.CorrelationId);

        // Kick off + poll + download NDJSON via the bulk-export client, then run the same Govern→Transform→Output.
        var exportResources = await _bulkExportClient.ExportAsync(request.Export, request.Source, cancellationToken);

        var resourceTypes = (request.Export.ResourceTypes is { Count: > 0 }
                ? request.Export.ResourceTypes
                : exportResources.Select(resource => resource.ResourceType))
            .Select(SupportedFhirResourceTypes.Normalize)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var pipelineRun = new PipelineRun(
            request.Source.SourceType,
            request.Destination.DestinationType,
            resourceTypes,
            request.TriggeredBy,
            request.CorrelationId);

        await _pipelineRunStore.AddAsync(pipelineRun, cancellationToken);
        await AddEventAsync(
            pipelineRun,
            "BulkExportStarted",
            null,
            null,
            null,
            $"Bulk export returned {exportResources.Count} resource(s) across {resourceTypes.Length} type(s).",
            cancellationToken);

        try
        {
            var destinationWriter = _destinationWriterFactory.Create(request.Destination.DestinationType);
            var transformedResources = new List<ResourceEnvelope>();

            foreach (var resourceType in resourceTypes)
            {
                IReadOnlyList<ResourceEnvelope> typeResources = exportResources
                    .Where(resource => string.Equals(resource.ResourceType, resourceType, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                await RecordExtractionAsync(pipelineRun, resourceType, typeResources, cancellationToken);
                await GovernAsync(pipelineRun, resourceType, typeResources, cancellationToken);
                transformedResources.AddRange(await TransformAsync(pipelineRun, resourceType, typeResources, cancellationToken));
            }

            await WriteOutputAsync(pipelineRun, destinationWriter, request.Destination, transformedResources, cancellationToken);

            pipelineRun.Complete();
            await AddEventAsync(
                pipelineRun,
                "PipelineCompleted",
                null,
                null,
                null,
                $"Bulk export run completed. Extracted {pipelineRun.ExtractedResourceCount}; wrote {pipelineRun.WrittenResourceCount}.",
                cancellationToken);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Runtime bulk export run {PipelineRunId} failed.", pipelineRun.Id);
            pipelineRun.Fail(exception.Message);

            await CaptureFailureAsync(pipelineRun, exception, cancellationToken);

            await AddEventAsync(
                pipelineRun,
                "PipelineFailed",
                null,
                null,
                null,
                "Bulk export run failed. See failure message on the run record.",
                cancellationToken);
        }

        await _pipelineRunStore.UpdateAsync(pipelineRun, cancellationToken);
        return PipelineDtoMapper.ToDto(pipelineRun);
    }

    // Persists the failure into the shared ErrorLog store (via GlobalExceptionManager) so it surfaces on both the
    // Errors screen and Correlation Search, not just as an ErrorMessage on this PipelineRun record.
    private Task CaptureFailureAsync(PipelineRun pipelineRun, Exception exception, CancellationToken cancellationToken)
    {
        if (_exceptionManager is null)
        {
            return Task.CompletedTask;
        }

        return _exceptionManager.CaptureAsync(
            exception,
            new ExceptionContext(
                Module: "Pipeline Run",
                CorrelationId: pipelineRun.CorrelationId,
                ExecutionId: pipelineRun.Id.ToString()),
            cancellationToken);
    }

    // Records the bookkeeping for an already-completed (parallel) extraction. The source round-trip happens in the
    // fan-out; this runs on the single fan-in thread so run-state mutation stays deterministic.
    private async Task RecordExtractionAsync(
        PipelineRun pipelineRun,
        string resourceType,
        IReadOnlyCollection<ResourceEnvelope> resources,
        CancellationToken cancellationToken)
    {
        var step = pipelineRun.StartStep(PipelineStepType.Extraction, resourceType);

        pipelineRun.AddExtractedResources(resources.Count);
        step.Complete(resources.Count, $"Extracted {resources.Count} {resourceType} resource(s).");
        await _pipelineRunStore.UpdateAsync(pipelineRun, cancellationToken);

        await AddEventAsync(
            pipelineRun,
            "ExtractionCompleted",
            PipelineStepType.Extraction,
            resourceType,
            null,
            $"Extracted {resources.Count} {resourceType} resource(s).",
            cancellationToken);
    }

    private async Task GovernAsync(
        PipelineRun pipelineRun,
        string resourceType,
        IReadOnlyCollection<ResourceEnvelope> resources,
        CancellationToken cancellationToken)
    {
        var step = pipelineRun.StartStep(PipelineStepType.Governance, resourceType);
        await _pipelineRunStore.UpdateAsync(pipelineRun, cancellationToken);

        foreach (var resource in resources)
        {
            await AddEventAsync(
                pipelineRun,
                "ResourceAccessed",
                PipelineStepType.Governance,
                resource.ResourceType,
                resource.ResourceId,
                "FHIR resource access recorded without resource payload or clinical content.",
                cancellationToken);
        }

        step.Complete(resources.Count, $"Recorded PHI-free audit entries for {resources.Count} {resourceType} resource(s).");
    }

    private async Task<IReadOnlyList<ResourceEnvelope>> TransformAsync(
        PipelineRun pipelineRun,
        string resourceType,
        IReadOnlyCollection<ResourceEnvelope> resources,
        CancellationToken cancellationToken)
    {
        var step = pipelineRun.StartStep(PipelineStepType.Transform, resourceType);
        await _pipelineRunStore.UpdateAsync(pipelineRun, cancellationToken);

        var transformed = await _resourceTransformer.TransformAsync(resourceType, resources, cancellationToken);

        step.Complete(transformed.Count, $"Normalized {transformed.Count} {resourceType} resource(s).");

        await AddEventAsync(
            pipelineRun,
            "TransformCompleted",
            PipelineStepType.Transform,
            resourceType,
            null,
            $"Normalized {transformed.Count} {resourceType} resource(s).",
            cancellationToken);

        return transformed;
    }

    private async Task WriteOutputAsync(
        PipelineRun pipelineRun,
        IDestinationWriter destinationWriter,
        RuntimeDestinationConfiguration destination,
        IReadOnlyCollection<ResourceEnvelope> resources,
        CancellationToken cancellationToken)
    {
        var step = pipelineRun.StartStep(PipelineStepType.Output, null);
        await _pipelineRunStore.UpdateAsync(pipelineRun, cancellationToken);

        var writtenCount = await destinationWriter.WriteAsync(pipelineRun, destination, resources, cancellationToken);

        pipelineRun.AddWrittenResources(writtenCount);
        step.Complete(writtenCount, $"Wrote {writtenCount} normalized FHIR resource(s).");

        await AddEventAsync(
            pipelineRun,
            "OutputCompleted",
            PipelineStepType.Output,
            null,
            null,
            $"Wrote {writtenCount} normalized FHIR resource(s).",
            cancellationToken);
    }

    private Task AddEventAsync(
        PipelineRun pipelineRun,
        string eventType,
        PipelineStepType? stepType,
        string? resourceType,
        string? resourceId,
        string message,
        CancellationToken cancellationToken)
    {
        var pipelineRunEvent = new PipelineRunEvent(
            pipelineRun.Id,
            eventType,
            stepType,
            resourceType,
            resourceId,
            message,
            pipelineRun.CorrelationId);

        return _pipelineRunStore.AddEventAsync(pipelineRunEvent, cancellationToken);
    }
}

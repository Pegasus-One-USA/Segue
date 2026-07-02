using FHIRBridge.Application.Abstractions.Destinations;
using FHIRBridge.Application.Abstractions.Mapping;
using FHIRBridge.Application.Abstractions.Normalization;
using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Application.Mappings;
using FHIRBridge.Domain.Aggregates;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Domain.Enums;
using FHIRBridge.Runtime.Domain.ValueObjects;
using FHIRBridge.SharedKernel.Exceptions;
using Microsoft.Extensions.Logging;

namespace FHIRBridge.Infrastructure.Destinations;

/// <summary>
/// Formats a pass-through read into the tenant's admin-configured destination format. Reuses the exact map + write
/// path the configured pipeline uses (<see cref="IJsonMappingEngine"/> + <see cref="IMappedRecordNormalizationService"/>
/// + <see cref="IConfiguredDestinationWriterFactory"/>) so behaviour is consistent, but drives it from a caller read
/// rather than a PipelineRun. Inline text formats (CSV, NDJSON) are returned in the response body; every other
/// (sink) format is written to the configured target and reported by count. Returns null when nothing is configured.
/// </summary>
public sealed class PassthroughExportWriter : IPassthroughExportWriter
{
    private readonly ITenantConfigurationRepository _tenantRepository;
    private readonly IJsonMappingEngine _mappingEngine;
    private readonly IMappedRecordNormalizationService _normalization;
    private readonly IConfiguredDestinationWriterFactory _writerFactory;
    private readonly ILogger<PassthroughExportWriter> _logger;

    public PassthroughExportWriter(
        ITenantConfigurationRepository tenantRepository,
        IJsonMappingEngine mappingEngine,
        IMappedRecordNormalizationService normalization,
        IConfiguredDestinationWriterFactory writerFactory,
        ILogger<PassthroughExportWriter> logger)
    {
        _tenantRepository = tenantRepository;
        _mappingEngine = mappingEngine;
        _normalization = normalization;
        _writerFactory = writerFactory;
        _logger = logger;
    }

    public async Task<PassthroughExportResult?> WriteAsync(
        Guid tenantId,
        Guid? destinationId,
        IReadOnlyCollection<ResourceEnvelope> resources,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resources);
        if (resources.Count == 0)
        {
            return null;
        }

        var tenant = await _tenantRepository.GetByIdAsync(tenantId, cancellationToken);
        if (tenant is null)
        {
            return null;
        }

        var destination = ResolveDestination(tenant, destinationId);
        if (destination is null)
        {
            // No destination configured, or ambiguous with no explicit id — caller falls back to the FHIR Bundle.
            return null;
        }

        // Mapping profiles bound to this destination, grouped so each is written with its own profile (as the pipeline
        // does). A profile maps exactly one resource type.
        var profiles = tenant.MappingProfiles
            .Where(profile => profile.IsEnabled && profile.DestinationId == destination.Id)
            .ToList();
        if (profiles.Count == 0)
        {
            return null;
        }

        var groups = new List<(MappingProfile Profile, List<MappedDestinationRecord> Records)>();
        foreach (var profile in profiles)
        {
            var records = await MapForProfileAsync(tenantId, profile, resources, cancellationToken);
            if (records.Count > 0)
            {
                groups.Add((profile, records));
            }
        }

        if (groups.Count == 0)
        {
            return null;
        }

        var allRecords = groups.SelectMany(group => group.Records).ToList();

        // Inline text formats are serialized straight into the response body; every other format is written to the
        // configured sink via the same writers the pipeline uses. (Switch is on DestinationType — permitted by the
        // no-switch architecture rule, which only forbids RuntimeSourceType / ApplicationType.)
        switch (destination.DestinationType)
        {
            case DestinationType.Csv:
                return new PassthroughExportResult(
                    Inline: true,
                    Content: MappedDestinationSerialization.ToCsv(allRecords),
                    ContentType: "text/csv",
                    FileName: "patient-export.csv",
                    RecordCount: allRecords.Count,
                    WrittenCount: allRecords.Count,
                    Format: nameof(DestinationType.Csv));

            case DestinationType.Ndjson:
                return new PassthroughExportResult(
                    Inline: true,
                    Content: MappedDestinationSerialization.ToNdjson(allRecords),
                    ContentType: "application/x-ndjson",
                    FileName: "patient-export.ndjson",
                    RecordCount: allRecords.Count,
                    WrittenCount: allRecords.Count,
                    Format: nameof(DestinationType.Ndjson));

            default:
                var written = 0;
                foreach (var (profile, records) in groups)
                {
                    var writer = _writerFactory.Create(destination.DestinationType);
                    written += await writer.WriteAsync(destination, profile, records, cancellationToken);
                }

                return new PassthroughExportResult(
                    Inline: false,
                    Content: null,
                    ContentType: "application/json",
                    FileName: null,
                    RecordCount: allRecords.Count,
                    WrittenCount: written,
                    Format: destination.DestinationType.ToString());
        }
    }

    private async Task<List<MappedDestinationRecord>> MapForProfileAsync(
        Guid tenantId,
        MappingProfile profile,
        IReadOnlyCollection<ResourceEnvelope> resources,
        CancellationToken cancellationToken)
    {
        // Same field selection as ConfiguredPipelineService.MapResourcesAsync.
        var mappingFields = profile.Fields
            .Select(TenantConfigurationMapper.ToDto)
            .Where(field =>
                string.IsNullOrWhiteSpace(field.ResourceType) ||
                string.Equals(field.ResourceType, profile.ResourceType, StringComparison.OrdinalIgnoreCase))
            .Where(field =>
                string.IsNullOrWhiteSpace(field.DestinationObject) ||
                string.Equals(field.DestinationObject, profile.DestinationObject, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var records = new List<MappedDestinationRecord>();
        foreach (var resource in resources.Where(r =>
                     string.Equals(r.ResourceType, profile.ResourceType, StringComparison.OrdinalIgnoreCase)))
        {
            var result = _mappingEngine.Map(resource.RawJson, mappingFields);
            if (result.Errors.Count > 0)
            {
                _logger.LogWarning(
                    "Pass-through mapping for {ResourceType}/{ResourceId} produced errors; skipping that resource.",
                    profile.ResourceType,
                    resource.ResourceId ?? "unknown");
                continue;
            }

            // No PipelineRun for a pass-through read, so PipelineRunId is Guid.Empty.
            var mappedRecord = new MappedDestinationRecord(
                tenantId,
                Guid.Empty,
                profile.ResourceType,
                profile.DestinationObject,
                resource.ResourceId,
                result.Values,
                resource.RawJson);

            records.Add(await _normalization.NormalizeAsync(
                new MappedRecordNormalizationRequest(resource.RawJson, mappedRecord, mappingFields),
                cancellationToken));
        }

        return records;
    }

    private static DestinationConfiguration? ResolveDestination(Tenant tenant, Guid? destinationId)
    {
        if (destinationId is Guid id)
        {
            return tenant.DestinationConfigurations.FirstOrDefault(d => d.Id == id && d.IsEnabled)
                ?? throw new NotFoundException("DestinationConfiguration", id);
        }

        var enabled = tenant.DestinationConfigurations.Where(d => d.IsEnabled).ToList();
        return enabled.Count == 1 ? enabled[0] : null;
    }
}

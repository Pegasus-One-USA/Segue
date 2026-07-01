using FHIRBridge.Application.Abstractions.Aggregation;
using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.Services;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Domain.Enums;
using FHIRBridge.Runtime.Application.Abstractions.Connectors;
using FHIRBridge.Runtime.Application.DTOs;
using FHIRBridge.Runtime.Application.Pipeline;
using FHIRBridge.Runtime.Domain.Enums;
using FHIRBridge.Runtime.Domain.ValueObjects;
using FHIRBridge.SharedKernel.Exceptions;
using Microsoft.Extensions.Logging;

namespace FHIRBridge.Infrastructure.Aggregation;

/// <summary>
/// Best-effort, pass-through implementation of <see cref="IPatientAggregationService"/>. Resolves the tenant's
/// source connection, scopes one query per requested resource type (plus the Patient root), and fans them out with
/// a bounded degree of parallelism. Reuses the runtime connector layer (auth, pagination, retry, throttle) via
/// <see cref="IFhirSourceClient"/> — it does not touch the write-side pipeline.
/// </summary>
public sealed class PatientAggregationService : IPatientAggregationService
{
    private readonly ITenantConfigurationRepository _tenantRepository;
    private readonly IFhirSourceClientFactory _sourceClientFactory;
    private readonly ISecretProvider _secretProvider;
    private readonly PatientAggregationOptions _options;
    private readonly ILogger<PatientAggregationService> _logger;

    public PatientAggregationService(
        ITenantConfigurationRepository tenantRepository,
        IFhirSourceClientFactory sourceClientFactory,
        ISecretProvider secretProvider,
        ILogger<PatientAggregationService> logger,
        PatientAggregationOptions? options = null)
    {
        _tenantRepository = tenantRepository;
        _sourceClientFactory = sourceClientFactory;
        _secretProvider = secretProvider;
        _options = options ?? PatientAggregationOptions.Default;
        _logger = logger;
    }

    public async Task<PatientAggregationResult> GetEverythingAsync(
        Guid tenantId,
        string patientId,
        IReadOnlyCollection<string> resourceTypes,
        Guid? sourceConnectionId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resourceTypes);
        if (string.IsNullOrWhiteSpace(patientId))
        {
            throw new ArgumentException("Patient id is required.", nameof(patientId));
        }

        var tenant = await _tenantRepository.GetByIdAsync(tenantId, cancellationToken)
            ?? throw new NotFoundException("Tenant", tenantId);

        var sourceConnection = ResolveSourceConnection(tenant.SourceConnections, sourceConnectionId);

        // One query per type: the Patient root (_id) plus each requested compartment type (patient=).
        var queries = new List<ResourceQuery>(resourceTypes.Count + 1)
        {
            new("Patient", $"_id={patientId}")
        };
        queries.AddRange(resourceTypes
            .Where(type => !string.Equals(type, "Patient", StringComparison.OrdinalIgnoreCase))
            .Select(type => new ResourceQuery(type, $"patient={patientId}")));

        var queryResults = await ParallelFanOut.RunAsync(
            queries,
            _options.MaxQueryParallelism,
            (query, ct) => ExecuteQueryAsync(sourceConnection, query, ct),
            cancellationToken);

        var resources = new List<ResourceEnvelope>();
        var failures = new List<ResourceFetchFailure>();
        foreach (var result in queryResults)
        {
            resources.AddRange(result.Resources);
            if (result.Failure is not null)
            {
                failures.Add(result.Failure);
            }
        }

        return new PatientAggregationResult(resources, failures);
    }

    private async Task<QueryResult> ExecuteQueryAsync(
        SourceConnection sourceConnection,
        ResourceQuery query,
        CancellationToken cancellationToken)
    {
        try
        {
            var configuration = await BuildSourceConfigurationAsync(sourceConnection, query.SearchParameters, cancellationToken);
            var client = _sourceClientFactory.Create(configuration.SourceType);
            var resources = await client.SearchAsync(query.ResourceType, configuration, cancellationToken);
            return new QueryResult(resources, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // Best-effort: a single type's failure is recorded and surfaced as an OperationOutcome, never thrown.
            _logger.LogWarning(
                ex,
                "Patient aggregation query for resource type {ResourceType} failed; continuing best-effort.",
                query.ResourceType);
            return new QueryResult([], new ResourceFetchFailure(query.ResourceType, ex.Message));
        }
    }

    private static SourceConnection ResolveSourceConnection(
        IReadOnlyCollection<SourceConnection> sourceConnections,
        Guid? sourceConnectionId)
    {
        if (sourceConnectionId is Guid id)
        {
            var requested = sourceConnections.FirstOrDefault(x => x.Id == id)
                ?? throw new NotFoundException("SourceConnection", id);
            if (!requested.IsEnabled)
            {
                throw new SourceConnectionUnavailableException($"Source connection '{id}' is disabled.");
            }

            return requested;
        }

        var enabled = sourceConnections.Where(x => x.IsEnabled).ToList();
        return enabled.Count switch
        {
            0 => throw new SourceConnectionUnavailableException("The tenant has no enabled source connection."),
            1 => enabled[0],
            _ => throw new SourceConnectionUnavailableException(
                "The tenant has multiple enabled source connections; specify which source to read from."),
        };
    }

    // Mirrors ConfiguredPipelineService.BuildSourceConfigurationAsync (write-side) — kept local so the read path
    // does not depend on the pipeline service. Scopes the source with the per-query search parameters.
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
            _ => throw new NotSupportedException($"Source system '{sourceConnection.SourceSystemType}' is not supported for patient aggregation.")
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
            _options.SearchCount,
            _options.MaxPages,
            sourceConnection.TenantId,
            sourceConnection.Id,
            searchParameters,
            clientSecret);
    }

    private sealed record ResourceQuery(string ResourceType, string SearchParameters);

    private sealed record QueryResult(IReadOnlyList<ResourceEnvelope> Resources, ResourceFetchFailure? Failure);
}

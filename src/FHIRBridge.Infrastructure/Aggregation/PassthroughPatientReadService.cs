using FHIRBridge.Application.Abstractions.Aggregation;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Application.Services;
using FHIRBridge.Runtime.Application.Abstractions.Connectors;
using FHIRBridge.Runtime.Application.DTOs;
using FHIRBridge.Runtime.Application.Pipeline;
using FHIRBridge.Runtime.Domain.Enums;
using FHIRBridge.Runtime.Domain.ValueObjects;
using Microsoft.Extensions.Logging;

namespace FHIRBridge.Infrastructure.Aggregation;

/// <summary>
/// Stateless, caller-token patient read for the provider-standalone flow. Identical fan-out / best-effort semantics
/// to <see cref="PatientAggregationService"/>, but the access token is supplied by the caller (their app ran the
/// SMART login) instead of being minted by FHIRBridge — so it resolves NO stored source connection, hits NO token
/// endpoint, and persists nothing. Reuses the shared connector layer (<see cref="IFhirSourceClient"/>), the bounded
/// fan-out, and the same options; the caller token flows via <c>FhirSourceConfiguration.AccessToken</c>, which the
/// composite token provider returns verbatim. Intentionally kept separate from
/// <see cref="PatientAggregationService"/> so the existing stored-connection read is untouched.
/// </summary>
public sealed class PassthroughPatientReadService : IPassthroughPatientReadService
{
    private readonly IFhirSourceClientFactory _sourceClientFactory;
    private readonly PatientAggregationOptions _options;
    private readonly ILogger<PassthroughPatientReadService> _logger;

    public PassthroughPatientReadService(
        IFhirSourceClientFactory sourceClientFactory,
        PatientAggregationOptions options,
        ILogger<PassthroughPatientReadService> logger)
    {
        _sourceClientFactory = sourceClientFactory;
        _options = options ?? PatientAggregationOptions.Default;
        _logger = logger;
    }

    public async Task<PatientAggregationResult> GetEverythingAsync(
        CallerTokenReadContext context,
        IReadOnlyCollection<string> resourceTypes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(resourceTypes);
        if (string.IsNullOrWhiteSpace(context.AccessToken))
        {
            throw new ArgumentException("A caller access token is required.", nameof(context));
        }

        if (string.IsNullOrWhiteSpace(context.FhirBaseUrl))
        {
            throw new ArgumentException("A FHIR base URL is required.", nameof(context));
        }

        if (string.IsNullOrWhiteSpace(context.PatientId))
        {
            throw new ArgumentException("A patient id is required.", nameof(context));
        }

        var sourceType = MapEhr(context.Ehr);

        // One query per type: the Patient root (_id) plus each requested compartment type (patient=).
        var queries = new List<ResourceQuery>(resourceTypes.Count + 1)
        {
            new("Patient", $"_id={context.PatientId}")
        };
        queries.AddRange(resourceTypes
            .Where(type => !string.Equals(type, "Patient", StringComparison.OrdinalIgnoreCase))
            .Select(type => new ResourceQuery(type, $"patient={context.PatientId}")));

        var queryResults = await ParallelFanOut.RunAsync(
            queries,
            _options.MaxQueryParallelism,
            (query, ct) => ExecuteQueryAsync(context, sourceType, query, ct),
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
        CallerTokenReadContext context,
        RuntimeSourceType sourceType,
        ResourceQuery query,
        CancellationToken cancellationToken)
    {
        try
        {
            // The caller token flows via AccessToken; no ClientId/Secret/PrivateKey/TokenEndpoint — no OAuth occurs.
            var configuration = new FhirSourceConfiguration(
                SourceType: sourceType,
                Name: context.Ehr,
                BaseUrl: context.FhirBaseUrl,
                TokenEndpoint: null,
                ClientId: null,
                KeyId: null,
                PrivateKeyPem: null,
                Scopes: Array.Empty<string>(),
                SearchCount: _options.SearchCount,
                MaxPages: _options.MaxPages,
                SearchParameters: query.SearchParameters,
                AccessToken: context.AccessToken);

            var client = _sourceClientFactory.Create(configuration.SourceType);
            var resources = await client.SearchAsync(query.ResourceType, configuration, cancellationToken);
            return new QueryResult(resources, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // Best-effort: never log the token; a single type's failure is recorded and surfaced, never thrown.
            _logger.LogWarning(
                ex,
                "Pass-through query for resource type {ResourceType} failed; continuing best-effort.",
                query.ResourceType);
            return new QueryResult([], new ResourceFetchFailure(query.ResourceType, ex.Message));
        }
    }

    // Maps the app-supplied EHR key to the runtime connector. Unknown/disabled vendors surface a clear error rather
    // than silently reading from the wrong connector. New vendors are enabled in the connector factory registry.
    private static RuntimeSourceType MapEhr(string? ehr) => ehr?.Trim().ToLowerInvariant() switch
    {
        "epic" => RuntimeSourceType.Epic,
        "sample" => RuntimeSourceType.Sample,
        "cerner" => RuntimeSourceType.Cerner,
        "allscripts" => RuntimeSourceType.Allscripts,
        "healow" => RuntimeSourceType.Healow,
        "meditech" or "meditechgreenfield" => RuntimeSourceType.MeditechGreenfield,
        "generic" or "genericfhir" or "fhir" => RuntimeSourceType.GenericFhir,
        "athena" or "athenahealth" => RuntimeSourceType.GenericFhir,
        _ => throw new NotSupportedException($"EHR '{ehr}' is not supported for a pass-through read.")
    };

    private sealed record ResourceQuery(string ResourceType, string SearchParameters);

    private sealed record QueryResult(IReadOnlyList<ResourceEnvelope> Resources, ResourceFetchFailure? Failure);
}

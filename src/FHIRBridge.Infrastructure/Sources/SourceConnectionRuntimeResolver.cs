using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Domain.Enums;
using FHIRBridge.Domain.ValueObjects;
using FHIRBridge.Runtime.Application.Abstractions.Sources;
using FHIRBridge.Runtime.Application.DTOs;
using FHIRBridge.Runtime.Domain.Enums;

namespace FHIRBridge.Infrastructure.Sources;

/// <summary>
/// Loads a SourceConnection by id and maps it to the runtime <see cref="FhirSourceConfiguration"/> (resolving any
/// client-secret / private-key references), mirroring the route pipeline's source-config builder so a graph run and
/// a route run behave identically. Returns null when the connection does not exist so the caller can fall back.
/// </summary>
public sealed class SourceConnectionRuntimeResolver : ISourceConnectionRuntimeResolver
{
    private readonly IConfigurationRepository _repository;
    private readonly ISecretProvider _secretProvider;

    public SourceConnectionRuntimeResolver(
        IConfigurationRepository repository,
        ISecretProvider secretProvider)
    {
        _repository = repository;
        _secretProvider = secretProvider;
    }

    public async Task<FhirSourceConfiguration?> ResolveAsync(
        Guid sourceConnectionId,
        string? searchParameters,
        CancellationToken cancellationToken)
    {
        var sourceConnection = await _repository.GetSourceConnectionAsync(sourceConnectionId, cancellationToken);
        if (sourceConnection is null)
        {
            return null;
        }

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
            _ => throw new NotSupportedException($"Source system '{sourceConnection.SourceSystemType}' is not supported by the workflow engine.")
        };

        // A loopback base URL (e.g. docker-compose's local HAPI FHIR) has no real OAuth server to talk to — any
        // Token Endpoint configured against it is guaranteed to fail (either unreachable, or misinterpreted as a
        // FHIR REST call by a plain FHIR server). Treat loopback sources as unauthenticated: skip resolving
        // credential secrets and clear ApplicationType so CompositeFhirAccessTokenProvider's legacy inference falls
        // through to its no-credentials branch (empty token, no Authorization header) instead of routing into the
        // Backend Services JWT strategy. Real (non-loopback) sources are completely unaffected.
        var isLoopback = Uri.TryCreate(sourceConnection.BaseUrl, UriKind.Absolute, out var baseUri) && baseUri.IsLoopback;

        string? privateKeyPem = null;
        if (!isLoopback && sourceConnection.Authentication.PrivateKey is not null)
        {
            privateKeyPem = await _secretProvider.GetSecretAsync(sourceConnection.Authentication.PrivateKey, cancellationToken);
        }

        string? clientSecret = null;
        if (!isLoopback && sourceConnection.Authentication.ClientSecret is not null)
        {
            clientSecret = await _secretProvider.GetSecretAsync(sourceConnection.Authentication.ClientSecret, cancellationToken);
        }

        var retrieval = sourceConnection.Retrieval;
        var composedSearchParameters = ComposeSearchParameters(searchParameters, retrieval);

        return new FhirSourceConfiguration(
            sourceType,
            sourceConnection.Name,
            sourceConnection.BaseUrl,
            sourceConnection.Authentication.TokenEndpoint,
            sourceConnection.Authentication.ClientId,
            sourceConnection.Authentication.KeyId,
            privateKeyPem,
            sourceConnection.Authentication.Scopes,
            retrieval?.PageSize ?? 100,
            5,
            sourceConnection.Id,
            composedSearchParameters,
            clientSecret,
            ApplicationType: isLoopback ? null : sourceConnection.ApplicationType,
            ResourceTypes: retrieval?.ResourceTypes is { Length: > 0 } types ? types : null,
            MaxRecords: retrieval?.MaxRecordsPerRun,
            RetryPolicy: retrieval?.RetryPolicy,
            TimeoutSeconds: retrieval?.TimeoutSeconds);
    }

    /// <summary>
    /// Layers a Backend System retrieval configuration's search criteria, incremental cursor, sort, and
    /// include/revinclude settings onto the node-config search-parameters string. Returns the original string
    /// unchanged when there is no retrieval config (interactive sources, or Backend sources predating this field) —
    /// every existing connection keeps behaving exactly as before.
    /// </summary>
    private static string? ComposeSearchParameters(string? baseSearchParameters, SourceRetrievalConfiguration? retrieval)
    {
        if (retrieval is null)
        {
            return baseSearchParameters;
        }

        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(baseSearchParameters))
        {
            parts.Add(baseSearchParameters.Trim('&'));
        }

        if (!string.IsNullOrWhiteSpace(retrieval.SearchCriteria))
        {
            parts.Add(retrieval.SearchCriteria.Trim('&'));
        }

        if (retrieval.IncrementalSyncEnabled && retrieval.LastSuccessfulSyncUtc is { } lastSync)
        {
            parts.Add($"_lastUpdated=gt{lastSync:yyyy-MM-ddTHH:mm:ssZ}");
        }

        if (!string.IsNullOrWhiteSpace(retrieval.SortOrder))
        {
            parts.Add($"_sort={retrieval.SortOrder}");
        }

        // FHIR search repeats the parameter for multiple _include/_revinclude directives (_include=A&_include=B) —
        // comma-joining them into one _include=A,B is not valid syntax and HAPI (and likely other servers) rejects
        // it with a 400. One `parts` entry per value achieves the repeated-parameter form once joined with '&' below.
        foreach (var include in retrieval.IncludeParameters)
        {
            parts.Add($"_include={include}");
        }

        foreach (var revInclude in retrieval.RevIncludeParameters)
        {
            parts.Add($"_revinclude={revInclude}");
        }

        return parts.Count == 0 ? null : string.Join('&', parts);
    }
}

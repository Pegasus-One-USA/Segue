using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.Services;
using FHIRBridge.Domain.Enums;
using FHIRBridge.Domain.ValueObjects;
using FHIRBridge.Runtime.Application.Abstractions.Auth;
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
    private readonly IScopeGeneratorService _scopeGenerator;
    // IFhirPatientContextProvider is never registered as its own service type — it's reached by downcasting the
    // registered IFhirAccessTokenProvider (CompositeFhirAccessTokenProvider implements both), the same pattern
    // FhirSourceConnectorBase.ApplyPatientScopeAsync already uses.
    private readonly IFhirAccessTokenProvider? _accessTokenProvider;

    public SourceConnectionRuntimeResolver(
        IConfigurationRepository repository,
        ISecretProvider secretProvider,
        IScopeGeneratorService scopeGenerator,
        IFhirAccessTokenProvider? accessTokenProvider = null)
    {
        _repository = repository;
        _secretProvider = secretProvider;
        _scopeGenerator = scopeGenerator;
        _accessTokenProvider = accessTokenProvider;
    }

    public async Task<FhirSourceConfiguration?> ResolveAsync(
        Guid sourceConnectionId,
        string? searchParameters,
        string? targetPatientId,
        CancellationToken cancellationToken,
        string? patientSearchCriteria = null,
        string? callerId = null)
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

        // A source connection that was created/re-saved without ever going through the wizard's scope preview (or
        // the admin resync endpoint — see IEpicSourceConnectionScopeSyncService) can reach here with an empty
        // persisted Authentication.Scopes list. Falling through to SmartAuthorizationCodeTokenProvider.ResolveScopes'
        // own last-resort default in that case is wrong for anything but a Patient-type source — it hardcodes
        // launch/patient + patient/*.read, which for a Standalone/EhrLaunch (Provider) source makes Epic show its
        // native patient-search screen instead of going straight to consent. Generating from this workflow's own
        // configured resource types (the same generator the wizard and the resync endpoint already use) keeps this
        // in sync with actual usage without requiring an admin to remember to resync.
        var scopes = sourceConnection.Authentication.Scopes.Any()
            ? sourceConnection.Authentication.Scopes
            : _scopeGenerator.Generate(
                sourceConnection.ApplicationType,
                retrieval?.ResourceTypes ?? [],
                scopeVersion: "v2",
                scopeVersionDetected: false,
                supportedScopes: null).Scopes;

        var config = new FhirSourceConfiguration(
            sourceType,
            sourceConnection.Name,
            sourceConnection.BaseUrl,
            sourceConnection.Authentication.TokenEndpoint,
            sourceConnection.Authentication.ClientId,
            sourceConnection.Authentication.KeyId,
            privateKeyPem,
            scopes,
            retrieval?.PageSize ?? 100,
            5,
            sourceConnection.Id,
            composedSearchParameters,
            clientSecret,
            ApplicationType: isLoopback ? null : sourceConnection.ApplicationType,
            ResourceTypes: retrieval?.ResourceTypes is { Length: > 0 } types ? types : null,
            MaxRecords: retrieval?.MaxRecordsPerRun,
            RetryPolicy: retrieval?.RetryPolicy,
            TimeoutSeconds: retrieval?.TimeoutSeconds,
            RetrievalMethod: retrieval?.RetrievalMethod,
            ExportScope: retrieval?.ExportScope,
            GroupId: retrieval?.GroupId,
            PatientIds: retrieval?.PatientIds is { Length: > 0 } patientIds ? patientIds : null,
            OutputFormat: retrieval?.OutputFormat,
            // Bulk $export uses the _since cursor (not the search path's _lastUpdated); carry it only when incremental
            // sync is on and a prior run recorded a timestamp.
            Since: retrieval is { IncrementalSyncEnabled: true, LastSuccessfulSyncUtc: { } lastSync }
                ? new DateTimeOffset(DateTime.SpecifyKind(lastSync, DateTimeKind.Utc))
                : null,
            TargetPatientId: targetPatientId,
            PatientSearchCriteria: patientSearchCriteria,
            CallerId: callerId);

        // For an interactive source whose launch resolved to a hospital/organization EhrEndpoint (rather than the
        // connection's own configured base URL), a later, separately triggered run must keep hitting that SAME
        // endpoint — it isn't stored on the SourceConnection row itself, only alongside the session's token. This is
        // a no-op for Backend System sources and for any interactive source whose launch never carried an override,
        // so every existing connection's behavior is unchanged.
        if (_accessTokenProvider is IFhirPatientContextProvider patientContextProvider)
        {
            var resolvedBaseUrl = await patientContextProvider.GetResolvedBaseUrlAsync(config, cancellationToken);
            if (!string.IsNullOrWhiteSpace(resolvedBaseUrl))
            {
                config = config with { BaseUrl = resolvedBaseUrl };
            }
        }

        return config;
    }

    public async Task DiscardTokenAsync(Guid sourceConnectionId, string? targetPatientId, CancellationToken cancellationToken, string? callerId = null)
    {
        if (_accessTokenProvider is not IFhirPatientContextProvider patientContextProvider)
        {
            return;
        }

        var sourceConnection = await _repository.GetSourceConnectionAsync(sourceConnectionId, cancellationToken);
        if (sourceConnection is null)
        {
            return;
        }

        // Only what BuildStoreKey/the strategy dispatch need — no secrets/base URL required to discard a cache entry.
        var source = new FhirSourceConfiguration(
            SourceType: default,
            Name: sourceConnection.Name,
            BaseUrl: null,
            TokenEndpoint: null,
            ClientId: null,
            KeyId: null,
            PrivateKeyPem: null,
            Scopes: [],
            SourceConnectionId: sourceConnection.Id,
            ApplicationType: sourceConnection.ApplicationType,
            TargetPatientId: targetPatientId,
            CallerId: callerId);

        await patientContextProvider.DiscardTokenAsync(source, cancellationToken);
    }

    // Reuses the exact same resolution ResolveAsync uses for a real run, then asks the dispatched token provider
    // (CompositeFhirAccessTokenProvider, routed by ApplicationType) whether it can produce a usable access token
    // right now — the same check GetAccessTokenAsync performs on every real fetch, just without going on to
    // actually search for resources. For an interactive source this is a cache lookup (silently refreshing via the
    // refresh token if the cached access token has merely expired); it only returns false when a genuinely fresh
    // interactive sign-in is required.
    public async Task<bool> HasValidTokenAsync(Guid sourceConnectionId, string? targetPatientId, CancellationToken cancellationToken, string? callerId = null)
    {
        if (_accessTokenProvider is null)
        {
            return false;
        }

        var source = await ResolveAsync(sourceConnectionId, searchParameters: null, targetPatientId, cancellationToken, callerId: callerId);
        if (source is null)
        {
            return false;
        }

        try
        {
            await _accessTokenProvider.GetAccessTokenAsync(source, cancellationToken);
            return true;
        }
        catch
        {
            return false;
        }
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
        var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(baseSearchParameters))
        {
            var trimmed = baseSearchParameters.Trim('&');
            parts.Add(trimmed);
            foreach (var segment in trimmed.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                seenKeys.Add(ExtractParameterKey(segment));
            }
        }

        if (!string.IsNullOrWhiteSpace(retrieval.SearchCriteria))
        {
            // The node-level "Search criteria" field is frequently a wizard-authored snapshot of this same
            // connection's SearchCriteria (see epic-audience-form.component.ts save()), not an independently
            // chosen addition — concatenating both unconditionally then re-sends the identical parameter twice
            // (e.g. "identifier=A,B&identifier=A,B"), which Epic rejects outright for identifier ("Don't support
            // searching by IDENTIFIER AND IDENTIFIER"). Only carry over parameters whose key isn't already present
            // in baseSearchParameters.
            var additional = retrieval.SearchCriteria.Trim('&')
                .Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Where(segment => seenKeys.Add(ExtractParameterKey(segment)))
                .ToList();

            if (additional.Count > 0)
            {
                parts.Add(string.Join('&', additional));
            }
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

    private static string ExtractParameterKey(string segment)
    {
        var equalsIndex = segment.IndexOf('=');
        return equalsIndex < 0 ? segment : segment[..equalsIndex];
    }
}

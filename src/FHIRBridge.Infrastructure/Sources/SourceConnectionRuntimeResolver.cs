using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.Services;
using FHIRBridge.Domain.Enums;
using FHIRBridge.Domain.ValueObjects;
using FHIRBridge.Runtime.Application.Abstractions.Auth;
using FHIRBridge.Runtime.Application.Abstractions.Sources;
using FHIRBridge.Runtime.Application.DTOs;
using FHIRBridge.Runtime.Domain.Enums;
using FHIRBridge.Observability.Logging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

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
    private readonly ILogger _logger;

    public SourceConnectionRuntimeResolver(
        IConfigurationRepository repository,
        ISecretProvider secretProvider,
        IScopeGeneratorService scopeGenerator,
        IFhirAccessTokenProvider? accessTokenProvider = null,
        ILogger<SourceConnectionRuntimeResolver>? logger = null)
    {
        _repository = repository;
        _secretProvider = secretProvider;
        _scopeGenerator = scopeGenerator;
        _accessTokenProvider = accessTokenProvider;
        _logger = logger ?? NullLogger<SourceConnectionRuntimeResolver>.Instance;
    }

    // Pages are a transport detail, not a data-volume policy: the operator's volume knob is Max Records Per Run,
    // and page size is theirs too, so the page cap is derived from both rather than hard-coded. It used to be a
    // bare "5", which with the default 100-record page silently capped EVERY resource type at 500 — no error, no
    // PartialSuccess, just a short count, and no setting anywhere to raise it (there is no MaxPages column). A
    // resource type with 501 records simply lost the rest.
    //
    // With Max Records Per Run set, that record cap is what bounds the run (SourceNodeExecutor trims to it), so no
    // single type needs more pages than can reach it. With it unset the operator asked for everything, so page
    // until the server stops offering a next link — bounded only by the backstop below, which exists to stop a
    // server that keeps handing out next links forever, not to cap legitimate data.
    private const int PageCountBackstop = 1000;

    private static int ResolveMaxPages(int pageSize, int? maxRecordsPerRun)
    {
        if (pageSize <= 0)
        {
            return PageCountBackstop;
        }

        if (maxRecordsPerRun is not { } maxRecords || maxRecords <= 0)
        {
            return PageCountBackstop;
        }

        // Ceiling division: a 250-record cap over 100-record pages still needs the partial third page.
        var pagesToReachCap = (maxRecords + pageSize - 1) / pageSize;
        return Math.Min(pagesToReachCap, PageCountBackstop);
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
            SourceSystemType.Athenahealth => RuntimeSourceType.Athenahealth,
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
            SigningKeySecretGuard.EnsurePemShaped(privateKeyPem, sourceConnection.Authentication.PrivateKey, sourceConnection.Name);
        }

        string? clientSecret = null;
        if (!isLoopback && sourceConnection.Authentication.ClientSecret is not null)
        {
            clientSecret = await _secretProvider.GetSecretAsync(sourceConnection.Authentication.ClientSecret, cancellationToken);
        }

        var retrieval = sourceConnection.Retrieval;
        var composedSearchParameters = ComposeSearchParameters(searchParameters, retrieval);

        // athenahealth's Backend System app registrations verified against the live preview sandbox are
        // provisioned with v1 coarse scopes only (system/{Type}.read) — v2 granular scopes (system/{Type}.rs)
        // get rejected by the token endpoint with "Invalid Scope". eClinicalWorks (Healow) has the same v1-only
        // requirement — confirmed against a live authorize attempt, which eCW rejected with invalid_scope for a
        // v2 (.rs) resource scope. Every other vendor keeps the v2 default (see the portal's identical vendor
        // check in ehr-vendor-source-form.component.ts).
        var scopeVersion = sourceConnection.SourceSystemType is SourceSystemType.Athenahealth or SourceSystemType.Healow
            ? "v1"
            : "v2";

        // Backend System / Provider Standalone sources own their resource-type list directly (Retrieval.ResourceTypes
        // — the same Resource Type picker Settings already exposes), so it is always regenerated fresh here rather
        // than trusted from whatever Authentication.Scopes last happened to persist. A stale/never-resynced scope
        // snapshot was a real, repeated failure mode (an edited Resource Type selection silently kept requesting the
        // OLD resource set's scopes, tripping providers — athenahealth included — that reject the whole token
        // request for a single unrecognized scope) — regenerating on every resolve makes the Resource Type picker
        // the single, always-correct source of truth, with no separate sync step to remember to run.
        //
        // Interactive-only sources (EHR launch / Patient) have no Retrieval config of their own — there is nothing
        // to regenerate FROM here, so they keep using whatever IEpicSourceConnectionScopeSyncService (destination-
        // union, on workflow save) or the wizard's own scope preview last persisted, falling back to a generated
        // default only when that's genuinely never been populated.
        var scopes = retrieval is not null
            ? _scopeGenerator.Generate(
                sourceConnection.ApplicationType,
                retrieval.ResourceTypes,
                scopeVersion: scopeVersion,
                scopeVersionDetected: false,
                supportedScopes: null,
                vendor: sourceConnection.SourceSystemType,
                isGroupExport: string.Equals(retrieval.ExportScope, "group", StringComparison.OrdinalIgnoreCase)).Scopes
            : sourceConnection.Authentication.Scopes.Any()
                ? sourceConnection.Authentication.Scopes
                : _scopeGenerator.Generate(
                    sourceConnection.ApplicationType,
                    [],
                    scopeVersion: scopeVersion,
                    scopeVersionDetected: false,
                    supportedScopes: null,
                    vendor: sourceConnection.SourceSystemType).Scopes;

        // eClinicalWorks (Healow) rejects the ENTIRE token request (400 invalid_scope) if it carries a single resource
        // scope its app registration doesn't grant, and it accepts no wildcard — so the requested set MUST be a subset
        // of eCW's registered resource types. The scope list above is regenerated from Retrieval.ResourceTypes (or a
        // stored snapshot), either of which can carry types eCW never registered (the portal's resource picker and the
        // destination-driven scope sync both widen it well past eCW's ~30). Cap it to the known eCW-supported set so a
        // broad or stale resource selection can't take down the whole grant. Non-resource scopes pass through untouched;
        // every eCW backend scope is system/{ResourceType}.read. (athenahealth handles this by keeping its resource
        // list trimmed instead; eCW gets a hard cap here because its registered set is fixed and not discoverable.)
        if (sourceConnection.SourceSystemType == SourceSystemType.Healow)
        {
            scopes = scopes.Where(IsEClinicalWorksSupportedScope).ToList();
        }

        _logger.LogInformation(
            LogEvents.SourceResolved,
            "SourceConnectionRuntimeResolver: resolved connection {SourceConnectionId} ({SourceSystemType}) — " +
            "baseUrl={BaseUrl} tokenEndpoint={TokenEndpoint} practiceId={PracticeId} authPlacement={AuthPlacement} " +
            "scopesFrom={ScopesFrom} scope=\"{Scope}\"",
            sourceConnection.Id, sourceConnection.SourceSystemType, sourceConnection.BaseUrl,
            sourceConnection.Authentication.TokenEndpoint, sourceConnection.Authentication.PracticeId,
            sourceConnection.Authentication.AuthPlacement,
            retrieval is not null
                ? $"regenerated-from-retrieval({scopeVersion})"
                : sourceConnection.Authentication.Scopes.Any() ? "stored" : $"fallback-generated({scopeVersion})",
            string.Join(' ', scopes));

        var pageSize = retrieval?.PageSize ?? 100;

        var config = new FhirSourceConfiguration(
            sourceType,
            sourceConnection.Name,
            sourceConnection.BaseUrl,
            sourceConnection.Authentication.TokenEndpoint,
            sourceConnection.Authentication.ClientId,
            sourceConnection.Authentication.KeyId,
            privateKeyPem,
            scopes,
            pageSize,
            ResolveMaxPages(pageSize, retrieval?.MaxRecordsPerRun),
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
            // sync is on and every configured resource type has a prior recorded timestamp (see
            // GetEarliestSuccessfulSyncUtc — a batched export job can't give one type a different _since than another).
            Since: retrieval is { IncrementalSyncEnabled: true } && retrieval.GetEarliestSuccessfulSyncUtc(retrieval.ResourceTypes) is { } lastSync
                ? new DateTimeOffset(DateTime.SpecifyKind(lastSync, DateTimeKind.Utc))
                : null,
            // Search REST fetches each resource type via its own independent request, so each tracks its own
            // _lastUpdated cursor instead of sharing one connection-wide value (see SourceNodeExecutors, which
            // looks this up per resource type when building each request).
            LastUpdatedWatermarks: retrieval is { IncrementalSyncEnabled: true }
                ? retrieval.LastSuccessfulSyncUtcByResourceType
                : null,
            TargetPatientId: targetPatientId,
            PatientSearchCriteria: patientSearchCriteria,
            CallerId: callerId,
            PracticeId: sourceConnection.Authentication.PracticeId,
            AuthPlacement: sourceConnection.Authentication.AuthPlacement);

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
    // eCW's Bulk/Backend app registration grants exactly these system resource scopes (dev-portal "Selected Scopes",
    // confirmed 2026-09-01). eCW rejects any resource scope outside this set with 400 invalid_scope and fails the
    // whole token request, so ResolveAsync caps a Healow source's requested scopes to this list. Update if eCW's
    // registration changes. Kept here (like the Healow v1-only special-case above) rather than discovered, because
    // eCW's discovery does not advertise a usable scopes_supported to intersect against.
    private static readonly HashSet<string> EClinicalWorksSupportedResourceTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "AllergyIntolerance", "Binary", "CarePlan", "CareTeam", "Condition", "Coverage", "Device", "DiagnosticReport",
        "DocumentReference", "Encounter", "Goal", "Group", "Immunization", "Location", "Media", "Medication",
        "MedicationAdministration", "MedicationDispense", "MedicationRequest", "Observation", "Organization", "Patient",
        "Practitioner", "PractitionerRole", "Procedure", "Provenance", "QuestionnaireResponse", "RelatedPerson",
        "ServiceRequest", "Specimen",
    };

    // Keeps any non-resource scope (openid/fhirUser/offline_access/launch — no '/'), and any "system/{ResourceType}.*"
    // scope whose resource type is in the eCW-supported set. Used only for Healow (see ResolveAsync).
    private static bool IsEClinicalWorksSupportedScope(string scope)
    {
        var slash = scope.IndexOf('/');
        if (slash < 0)
        {
            return true;
        }

        var rest = scope[(slash + 1)..];
        var dot = rest.LastIndexOf('.');
        var resourceType = dot < 0 ? rest : rest[..dot];
        return EClinicalWorksSupportedResourceTypes.Contains(resourceType);
    }

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

        // _lastUpdated is intentionally NOT added here: each resource type is fetched via its own independent
        // search request with its own watermark (source.LastUpdatedWatermarks, applied per-type by
        // SourceNodeExecutors), rather than one value shared across every resource type this connection is
        // configured for.

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

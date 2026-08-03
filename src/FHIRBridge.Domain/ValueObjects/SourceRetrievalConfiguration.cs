namespace FHIRBridge.Domain.ValueObjects;

/// <summary>
/// How a Backend System source connection retrieves resources — currently only the Search (REST) polling method is
/// executed; Subscription/Webhook/Bulk Export values are accepted and persisted for forward-compatibility but are not
/// yet consumed by the workflow engine. Null on interactive (EHR launch / standalone / patient) sources, where
/// retrieval is driven by the launch context rather than a configured poll.
/// </summary>
public sealed class SourceRetrievalConfiguration
{
    private SourceRetrievalConfiguration()
    {
    }

    public SourceRetrievalConfiguration(
        string retrievalMethod,
        string[] resourceTypes,
        string? searchCriteria,
        bool incrementalSyncEnabled,
        int? pageSize = null,
        string? sortOrder = null,
        string[]? includeParameters = null,
        string[]? revIncludeParameters = null,
        string? retryPolicy = null,
        int? timeoutSeconds = null,
        int? maxRecordsPerRun = null,
        IReadOnlyDictionary<string, DateTime>? lastSuccessfulSyncUtcByResourceType = null,
        string? exportScope = null,
        string? groupId = null,
        string[]? patientIds = null,
        string? outputFormat = null)
    {
        RetrievalMethod = retrievalMethod;
        ResourceTypes = resourceTypes ?? [];
        SearchCriteria = searchCriteria;
        IncrementalSyncEnabled = incrementalSyncEnabled;
        PageSize = pageSize;
        SortOrder = sortOrder;
        IncludeParameters = includeParameters ?? [];
        RevIncludeParameters = revIncludeParameters ?? [];
        RetryPolicy = retryPolicy;
        TimeoutSeconds = timeoutSeconds;
        MaxRecordsPerRun = maxRecordsPerRun;
        LastSuccessfulSyncUtcByResourceType = lastSuccessfulSyncUtcByResourceType is { Count: > 0 }
            ? new Dictionary<string, DateTime>(lastSuccessfulSyncUtcByResourceType, StringComparer.OrdinalIgnoreCase)
            : EmptySyncMap;
        ExportScope = exportScope;
        GroupId = groupId;
        PatientIds = patientIds ?? [];
        OutputFormat = outputFormat;
    }

    private static readonly IReadOnlyDictionary<string, DateTime> EmptySyncMap =
        new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

    public string RetrievalMethod { get; private set; } = default!;
    public string[] ResourceTypes { get; private set; } = [];
    public string? SearchCriteria { get; private set; }
    public bool IncrementalSyncEnabled { get; private set; }
    public int? PageSize { get; private set; }
    public string? SortOrder { get; private set; }
    public string[] IncludeParameters { get; private set; } = [];
    public string[] RevIncludeParameters { get; private set; } = [];
    public string? RetryPolicy { get; private set; }
    public int? TimeoutSeconds { get; private set; }
    public int? MaxRecordsPerRun { get; private set; }

    // ── Bulk Data $export settings (RetrievalMethod == "bulk-export" only) ──────────────────────────
    /// <summary>Which $export variant to run: <c>system</c>, <c>patient</c>, or <c>group</c>. Null on non-bulk methods.</summary>
    public string? ExportScope { get; private set; }

    /// <summary>The Epic Group/registry FHIR id for a group-scoped export. Required when <see cref="ExportScope"/> is <c>group</c>.</summary>
    public string? GroupId { get; private set; }

    /// <summary>Patient FHIR ids to narrow a patient-scoped export (POST <c>Patient/$export</c> with a <c>patient</c> Parameters list). Empty = all patients.</summary>
    public string[] PatientIds { get; private set; } = [];

    /// <summary>Requested bulk output format, e.g. <c>application/fhir+ndjson</c>. Null lets the server pick its default.</summary>
    public string? OutputFormat { get; private set; }

    /// <summary>UTC timestamp of the last run that completed successfully, per resource type — the fallback
    /// incremental cursor when the connector doesn't expose its own resume token. Each resource type is fetched via
    /// its own independent FHIR search request, so each tracks its own watermark rather than sharing one connection-
    /// wide value; a type that was skipped or failed on a run keeps its prior entry untouched. Advanced only by
    /// <see cref="Entities.SourceConnection.RecordRetrievalSync"/>.</summary>
    public IReadOnlyDictionary<string, DateTime> LastSuccessfulSyncUtcByResourceType { get; private set; } = EmptySyncMap;

    /// <summary>The last successful sync watermark for a specific resource type, or null if that type has never
    /// completed a run (i.e. its next fetch should be a full pull).</summary>
    public DateTime? GetLastSuccessfulSyncUtc(string resourceType) =>
        LastSuccessfulSyncUtcByResourceType.TryGetValue(resourceType, out var syncedAtUtc) ? syncedAtUtc : null;

    /// <summary>The earliest sync watermark across the given resource types, for retrieval methods that only support
    /// one cursor per request (bulk <c>$export</c>'s single job-level <c>_since</c> covering several resource types
    /// at once) rather than per-type search requests. Conservative: if any of the given resource types has never
    /// completed a run, there is no watermark that's safe for the whole job, so this returns null (full pull) rather
    /// than silently skipping that type up to a sibling's more-advanced cursor.</summary>
    public DateTime? GetEarliestSuccessfulSyncUtc(IReadOnlyCollection<string> resourceTypes)
    {
        if (resourceTypes.Count == 0)
        {
            return null;
        }

        DateTime? earliest = null;
        foreach (var resourceType in resourceTypes)
        {
            if (!LastSuccessfulSyncUtcByResourceType.TryGetValue(resourceType, out var syncedAtUtc))
            {
                return null;
            }

            if (earliest is null || syncedAtUtc < earliest)
            {
                earliest = syncedAtUtc;
            }
        }

        return earliest;
    }

    /// <summary>Returns a copy with the given resource types' sync cursors advanced to <paramref name="syncedAtUtc"/>;
    /// every other resource type's existing cursor, and every other setting, is carried over unchanged.</summary>
    public SourceRetrievalConfiguration WithLastSuccessfulSync(IReadOnlyCollection<string> resourceTypes, DateTime syncedAtUtc)
    {
        var merged = new Dictionary<string, DateTime>(LastSuccessfulSyncUtcByResourceType, StringComparer.OrdinalIgnoreCase);
        foreach (var resourceType in resourceTypes)
        {
            merged[resourceType] = syncedAtUtc;
        }

        return new(
            RetrievalMethod, ResourceTypes, SearchCriteria, IncrementalSyncEnabled, PageSize, SortOrder,
            IncludeParameters, RevIncludeParameters, RetryPolicy, TimeoutSeconds, MaxRecordsPerRun, merged,
            ExportScope, GroupId, PatientIds, OutputFormat);
    }
}

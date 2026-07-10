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
        DateTime? lastSuccessfulSyncUtc = null)
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
        LastSuccessfulSyncUtc = lastSuccessfulSyncUtc;
    }

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

    /// <summary>UTC timestamp of the last run that completed successfully — the fallback incremental cursor when the
    /// connector doesn't expose its own resume token. Advanced only by <see cref="Entities.SourceConnection.RecordRetrievalSync"/>.</summary>
    public DateTime? LastSuccessfulSyncUtc { get; private set; }

    /// <summary>Returns a copy with the sync cursor advanced; all other settings are carried over unchanged.</summary>
    public SourceRetrievalConfiguration WithLastSuccessfulSync(DateTime syncedAtUtc) => new(
        RetrievalMethod, ResourceTypes, SearchCriteria, IncrementalSyncEnabled, PageSize, SortOrder,
        IncludeParameters, RevIncludeParameters, RetryPolicy, TimeoutSeconds, MaxRecordsPerRun, syncedAtUtc);
}

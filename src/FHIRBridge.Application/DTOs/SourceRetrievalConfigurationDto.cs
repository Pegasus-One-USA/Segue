namespace FHIRBridge.Application.DTOs;

/// <summary>
/// How a Backend System source connection retrieves resources. Only <c>search-rest</c> is executed by the workflow
/// engine today; other method values are accepted and persisted but not yet consumed. Null on interactive sources.
/// </summary>
public sealed record SourceRetrievalConfigurationDto(
    string RetrievalMethod,
    string[] ResourceTypes,
    string? SearchCriteria = null,
    bool IncrementalSyncEnabled = false,
    int? PageSize = null,
    string? SortOrder = null,
    string[]? IncludeParameters = null,
    string[]? RevIncludeParameters = null,
    string? RetryPolicy = null,
    int? TimeoutSeconds = null,
    int? MaxRecordsPerRun = null,
    IReadOnlyDictionary<string, DateTime>? LastSuccessfulSyncUtcByResourceType = null,
    string? ExportScope = null,
    string? GroupId = null,
    string[]? PatientIds = null,
    string? OutputFormat = null);

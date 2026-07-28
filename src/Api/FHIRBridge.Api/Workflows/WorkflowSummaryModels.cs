namespace FHIRBridge.Api.Workflows;

/// <summary>
/// One row of the workflow-list screen: the graph's shape (node/edge counts), its enabled state, the most recent run,
/// and — derived from the source node's referenced connection's <c>ApplicationType</c> — whether the workflow is
/// <c>Launch</c>ed (interactive SMART: EHR launch / standalone / patient) or <c>Run</c> (backend / non-interactive),
/// with the endpoint the UI should call for that action.
/// </summary>
public sealed record WorkflowSummaryDto(
    Guid WorkflowId,
    string Name,
    string Status,
    int Nodes,
    int Edges,
    string? LastRun,
    DateTimeOffset? LastRunAt,
    string Action,
    string ActionEndpoint,
    Guid? SourceConnectionId,
    string? SourceSystemType,
    string? ApplicationType,
    bool HasDestination,
    bool IsPubliclyLaunchable);

/// <summary>One server-side page of the workflow-list screen — <see cref="Items"/> is just this page's rows;
/// <see cref="TotalCount"/> is the count across every row matching the active search/filters (before paging), for
/// the portal's "Showing X-Y of Z" / page-count UI. The three Available* lists are the full distinct-value set
/// across every workflow (not just what's matching right now) so the portal's Status/Audience/Source multi-select
/// filter checkboxes have a stable option list regardless of what's currently checked.</summary>
public sealed record WorkflowSummaryPageDto(
    IReadOnlyList<WorkflowSummaryDto> Items,
    int TotalCount,
    IReadOnlyList<string> AvailableStatuses,
    IReadOnlyList<string> AvailableApplicationTypes,
    IReadOnlyList<string> AvailableSourceSystemTypes);

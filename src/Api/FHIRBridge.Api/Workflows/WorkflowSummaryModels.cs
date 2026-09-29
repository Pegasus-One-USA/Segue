namespace FHIRBridge.Api.Workflows;

/// <summary>
/// One row of the workflow-list screen: the graph's shape (node/edge counts), its lifecycle status, the most recent
/// run, and — derived from the source node's referenced connection's <c>ApplicationType</c> — whether the workflow is
/// <c>Launch</c>ed (interactive SMART: EHR launch / standalone / patient) or <c>Run</c> (backend / non-interactive),
/// with the endpoint the UI should call for that action.
/// </summary>
public sealed record WorkflowSummaryDto(
    Guid WorkflowId,
    string Name,
    /// <summary><c>Draft</c> (no destination wired up yet — not runnable), <c>Ready</c>, or <c>Disabled</c>
    /// (deliberately paused). See <see cref="FHIRBridge.Runtime.Domain.Workflows.WorkflowLifecycleStatus"/>.
    /// Replaces the former Enabled/Disabled pair, so <c>Enabled</c> is no longer a value this can take.</summary>
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
    bool IsPubliclyLaunchable,
    DateTime? CreatedOnUtc = null,
    string? CreatedBy = null,
    DateTime? ModifiedOnUtc = null,
    string? ModifiedBy = null,
    string? Description = null,
    /// <summary>Human-quotable sequential id (e.g. <c>WLW-150926-0042</c>), assigned at creation and stable
    /// for the workflow's life. Null for workflows created before numbering existed, or while numbering is
    /// switched off. Unlike <see cref="Name"/>, this is unique — it is what the UI quotes in confirmations.</summary>
    string? WorkflowNumber = null,
    /// <summary>Every distinct destination type this workflow writes to, as <see cref="FHIRBridge.Domain.Enums.DestinationType"/>
    /// names. A workflow can fan out to several destinations, so this is a list rather than the single value the
    /// Source columns carry — the Destination filter matches a row when ANY of its destinations is selected.
    /// Empty for a Draft whose destination node isn't wired to a destination record yet.</summary>
    IReadOnlyList<string>? DestinationTypes = null,
    /// <summary>Every distinct FHIR resource type this workflow's source nodes are configured to retrieve, read off
    /// the stored node configuration (never from what a past run happened to fetch). Empty when the source node
    /// derives its types implicitly — from granted SMART scopes or from its destinations — rather than naming
    /// them, which is why the Resource Type filter can legitimately not match every workflow.</summary>
    IReadOnlyList<string>? ResourceTypes = null,
    /// <summary>The run status of the most recent run, if any — same value as <see cref="LastRun"/>, restated here
    /// only so the facet contract reads symmetrically with the filter that consumes it.</summary>
    string? LastRunStatus = null);

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
    IReadOnlyList<string> AvailableSourceSystemTypes,
    /// <summary>Every destination type that is CONFIGURED in this tenant, from the destination catalog the workflow
    /// builder itself offers — not merely the ones some workflow already writes to. A destination the user just
    /// configured is therefore filterable immediately, before any workflow uses it.</summary>
    IReadOnlyList<string> AvailableDestinationTypes,
    /// <summary>Every <see cref="FHIRBridge.SharedKernel.Enums.ApplicationType"/> value, not just those in use, so
    /// the Audience filter offers the same fixed roster the builder's audience picker does.</summary>
    IReadOnlyList<string> AvailableLastRunStatuses,
    /// <summary>Every FHIR resource type named by any source node's stored configuration across the tenant.</summary>
    IReadOnlyList<string> AvailableResourceTypes);

using FHIRBridge.Runtime.Domain.Workflows;

namespace FHIRBridge.Api.Workflows;

public sealed record WorkflowDefinitionRequest(
    string Name,
    bool IsEnabled,
    IReadOnlyCollection<WorkflowNodeRequest> Nodes,
    IReadOnlyCollection<WorkflowEdgeRequest> Edges,
    WorkflowTriggerRequest? Trigger = null,
    bool IsPubliclyLaunchable = true);

/// <summary>Optional workflow-level scheduling metadata (Backend-Systems workflows). Omit / Manual = run on demand.</summary>
public sealed record WorkflowTriggerRequest(
    WorkflowTriggerType Type,
    string? ScheduleExpression = null,
    int? IntervalMinutes = null,
    bool BackfillOnFirstRun = false);

public sealed record WorkflowNodeRequest(
    string Id,
    string NodeType,
    WorkflowNodeCategory Category,
    int Rank,
    int SubRank,
    string? DisplayName,
    string? ConfigurationJson,
    double PositionX,
    double PositionY,
    bool IsEnabled,
    bool CheckpointUrlEnabled = false);

public sealed record WorkflowEdgeRequest(string FromNodeId, string ToNodeId);

public sealed record WorkflowRunRequest(
    string? CorrelationId,
    // Disambiguates which patient's stored interactive OAuth session (Standalone/EhrLaunch/Patient sources) this
    // run should use, when more than one patient has ever launched against the same source connection. Omit for
    // Backend System sources, or when only one patient has ever launched this source connection (the pre-existing,
    // single-session behavior applies).
    string? PatientId = null,
    // A third-party app's own free-text patient-search box (e.g. Demo_TestApp's Provider_Standalone screen) can
    // filter the Patient resource type search with any raw FHIR search criteria — "active=true",
    // "identifier=MRN12345", "family=Smith&given=John", "birthdate=1990-01-01" — instead of (or before) knowing a
    // specific PatientId. Passed through as-is (see FhirSourceConnectorBase.ApplyPatientScopeAsync); every other
    // configured resource type is unaffected. Omit for every existing caller/behavior.
    string? PatientSearchCriteria = null,
    // Identifies the logged-in end user of the calling third-party app (e.g. HealthApp's Patient Standalone
    // session). For an ApplicationType.Patient source, lets every pipeline that shares this same logged-in user's
    // session reuse the one interactive OAuth token their authorization already covers, instead of each
    // SourceConnection needing its own separate MyChart consent — see FhirSourceConfiguration.CallerId. Omit for
    // every other ApplicationType, or when this run should keep using the pre-existing per-SourceConnection session.
    string? CallerId = null,
    // When true, the run is dispatched to a background task and the endpoint returns 202 Accepted with the
    // run id immediately instead of blocking until the whole DAG finishes — see IWorkflowRunTracker. Poll
    // GET /workflow-runs/{runId}/status (or the existing /workflow-runs/{runId} once terminal) for progress.
    // Omit/false keeps the pre-existing blocking behavior.
    bool Async = false);

public sealed record WorkflowRunStatusResponse(Guid WorkflowRunId, string Status, string? CorrelationId = null);

public sealed record CopyWorkflowRequest(string Name);

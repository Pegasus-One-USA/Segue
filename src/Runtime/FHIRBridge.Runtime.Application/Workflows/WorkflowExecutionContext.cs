namespace FHIRBridge.Runtime.Application.Workflows;

public sealed class WorkflowExecutionContext
{
    public WorkflowExecutionContext(
        Guid workflowRunId,
        string correlationId,
        IReadOnlyDictionary<string, object?>? properties = null,
        string? triggeredBy = null,
        string? triggerType = null,
        string? targetPatientId = null,
        string? patientSearchCriteria = null,
        string? callerId = null,
        string? userIdentity = null,
        string? ehrEndpointCode = null,
        FHIRBridge.Runtime.Application.Abstractions.Sources.RunSourceOverrides? sourceOverrides = null)
    {
        WorkflowRunId = workflowRunId == Guid.Empty ? Guid.NewGuid() : workflowRunId;
        CorrelationId = string.IsNullOrWhiteSpace(correlationId) ? WorkflowRunId.ToString("N") : correlationId;
        Properties = properties ?? new Dictionary<string, object?>();
        TriggeredBy = triggeredBy;
        TriggerType = triggerType ?? "Manual";
        TargetPatientId = targetPatientId;
        PatientSearchCriteria = patientSearchCriteria;
        CallerId = callerId;
        UserIdentity = userIdentity;
        EhrEndpointCode = ehrEndpointCode;
        SourceOverrides = sourceOverrides;
    }

    public Guid WorkflowRunId { get; }

    public string CorrelationId { get; }

    public IReadOnlyDictionary<string, object?> Properties { get; }

    public string? TriggeredBy { get; }

    public string TriggerType { get; }

    /// <summary>
    /// Which patient's stored interactive OAuth session a source node should use for this run, when the workflow's
    /// source is an interactive (Standalone/EhrLaunch/Patient) connection more than one patient has ever launched
    /// against. Null (the default for every existing trigger path) preserves the pre-existing "most recently
    /// logged-in session" behavior.
    /// </summary>
    public string? TargetPatientId { get; }

    /// <summary>
    /// Request-time raw Patient search criteria (e.g. "active=true", "identifier=MRN12345",
    /// "family=Smith&amp;given=John", "birthdate=1990-01-01" — from a third-party app's own free-text search box).
    /// A source node executor appends this to the Patient resource type's search only, as-is; every other
    /// configured resource type is unaffected. Null/blank (the default for every existing trigger path) preserves
    /// prior behavior.
    /// </summary>
    public string? PatientSearchCriteria { get; }

    /// <summary>
    /// Identifies the logged-in end user of the calling third-party app (e.g. HealthApp's Patient Standalone
    /// session). For an ApplicationType.Patient source, this is used instead of SourceConnectionId to key the
    /// interactive OAuth token cache, so every pipeline that shares this same logged-in user's session reuses the
    /// one token their authorization already covers — see FhirSourceConfiguration.CallerId and
    /// SmartAuthorizationCodeTokenProvider.BuildStoreKey. Null (the default for every existing trigger path)
    /// preserves the pre-existing per-SourceConnection keying.
    /// </summary>
    public string? CallerId { get; }

    /// <summary>
    /// The stable identifier of the end user whose sign-in authorized this run — the calling third-party app's own
    /// account id for the person (e.g. ChartChat's internalPatientId), supplied as <c>userIdentity</c> when the
    /// launch URL was minted and permanently bound to one FHIR patient in <c>UserFhirContextBindings</c>.
    /// <para>
    /// Deliberately NOT the same thing as <see cref="CallerId"/> above, despite both identifying "who is driving
    /// this run": CallerId is an opaque per-browser session key used to look up the interactive token cache, so the
    /// same real person signing in from a second browser gets a different value. This one identifies the person
    /// across every launch, which is what makes it safe for a consumer to key its own records on. Anything that
    /// needs a durable identity must read this, not CallerId.
    /// </para>
    /// Null (the default for every existing trigger path) preserves prior behavior.
    /// </summary>
    public string? UserIdentity { get; }

    /// <summary>
    /// The vendor's own endpoint code (<c>EhrEndpoint.VendorEndpointId</c>), supplied only by the external
    /// browser-redirect trigger (<c>POST /api/v1/workflows/external/run</c>) when a caller wants to run
    /// against a specific hospital/practice endpoint rather than whatever a source node's own configuration
    /// already points at. Validated to be non-blank at the API layer; NOT YET consumed by any source node
    /// executor's own FHIR base-URL selection — that's vendor-specific follow-up work. Carried here (rather
    /// than left out) so it reaches every node executor once that work lands, without another context-shape
    /// change. Null (the default for every existing trigger path) preserves prior behavior.
    /// </summary>
    public string? EhrEndpointCode { get; }

    /// <summary>This run's Group ID / Search Criteria replacing the source connection's saved ones. Null for every
    /// ordinary run.</summary>
    public FHIRBridge.Runtime.Application.Abstractions.Sources.RunSourceOverrides? SourceOverrides { get; }

    /// <summary>Resolved by the orchestrator from <see cref="EhrEndpointCode"/> when the run explicitly names an EHR
    /// Endpoint. Null for every ordinary run — the source nodes then behave exactly as before.</summary>
    public FHIRBridge.Runtime.Application.Abstractions.Sources.EhrEndpointOverride? EhrEndpointOverride { get; private set; }

    public void SetEhrEndpointOverride(FHIRBridge.Runtime.Application.Abstractions.Sources.EhrEndpointOverride? value) =>
        EhrEndpointOverride = value;
}

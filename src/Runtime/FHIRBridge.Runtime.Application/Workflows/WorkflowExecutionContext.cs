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
        string? userIdentity = null)
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
}

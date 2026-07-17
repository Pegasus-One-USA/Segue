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
        string? patientSearchCriteria = null)
    {
        WorkflowRunId = workflowRunId == Guid.Empty ? Guid.NewGuid() : workflowRunId;
        CorrelationId = string.IsNullOrWhiteSpace(correlationId) ? WorkflowRunId.ToString("N") : correlationId;
        Properties = properties ?? new Dictionary<string, object?>();
        TriggeredBy = triggeredBy;
        TriggerType = triggerType ?? "Manual";
        TargetPatientId = targetPatientId;
        PatientSearchCriteria = patientSearchCriteria;
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
}

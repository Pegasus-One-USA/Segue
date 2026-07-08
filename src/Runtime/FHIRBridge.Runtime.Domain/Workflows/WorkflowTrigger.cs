namespace FHIRBridge.Runtime.Domain.Workflows;

/// <summary>
/// How a workflow is launched. <see cref="Manual"/> runs on demand (API/UI); <see cref="Schedule"/> fires on a cron
/// expression; <see cref="Poll"/> fires every N minutes. Interactive (EHR launch / standalone / patient) workflows
/// are always Manual — they are launched by a user, not scheduled. Backend-Systems workflows use Schedule or Poll.
/// </summary>
public enum WorkflowTriggerType
{
    Manual = 0,
    Schedule = 1,
    Poll = 2,
}

/// <summary>
/// Workflow-level scheduling metadata (approach B — not a graph execution node). Carried on the
/// <see cref="WorkflowDefinition"/> and read by the Worker to decide when to run the graph. Time-based only;
/// event-driven ingestion (webhook / FHIR Subscription) stays modelled on the source, not here.
/// </summary>
public sealed record WorkflowTrigger(
    WorkflowTriggerType Type,
    string? ScheduleExpression,
    int? IntervalMinutes,
    bool BackfillOnFirstRun);

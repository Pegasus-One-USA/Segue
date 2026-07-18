namespace FHIRBridge.Domain.Entities.Governance;

/// <summary>One real firing of an AlertRule. Not append-only — Acknowledged is expected to change, same
/// reasoning as SecurityEvent.Resolved.</summary>
public sealed class AlertHistoryEntry : SharedKernel.Abstractions.Entity<Guid>
{
    private AlertHistoryEntry()
    {
    }

    public AlertHistoryEntry(Guid id, Guid alertRuleId, string ruleName, string severity, string summary, DateTime firedOnUtc)
    {
        Id = id;
        AlertRuleId = alertRuleId;
        RuleName = ruleName;
        Severity = severity;
        Summary = summary;
        FiredOnUtc = firedOnUtc;
        Acknowledged = false;
    }

    public Guid AlertRuleId { get; private set; }
    public string RuleName { get; private set; } = default!;
    public string Severity { get; private set; } = default!;
    public string Summary { get; private set; } = default!;
    public DateTime FiredOnUtc { get; private set; }
    public bool Acknowledged { get; private set; }
    public DateTime? AcknowledgedOnUtc { get; private set; }
    public string? AcknowledgedBy { get; private set; }

    public void Acknowledge(string? acknowledgedBy, DateTime acknowledgedOnUtc)
    {
        Acknowledged = true;
        AcknowledgedBy = acknowledgedBy;
        AcknowledgedOnUtc = acknowledgedOnUtc;
    }
}

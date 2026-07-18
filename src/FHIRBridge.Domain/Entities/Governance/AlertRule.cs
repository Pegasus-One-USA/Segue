using FHIRBridge.SharedKernel.Abstractions;

namespace FHIRBridge.Domain.Entities.Governance;

/// <summary>
/// A single, real evaluatable condition: "N or more SecurityEvents matching EventTypeFilter within WindowMinutes"
/// — the exact shape the 100%-parity plan's example ("3+ failed logins in 15 minutes") already tracks via
/// SecurityEvents. Deliberately not a general expression language — one concrete, working condition rather than
/// a fake generic evaluator with nothing behind it.
/// </summary>
public sealed class AlertRule : AuditableChildEntity<Guid>
{
    private AlertRule()
    {
    }

    public AlertRule(
        Guid id,
        string name,
        string eventTypeFilter,
        int thresholdCount,
        int windowMinutes,
        string severity,
        string recipients,
        bool isEnabled)
    {
        Id = id;
        Name = name;
        EventTypeFilter = eventTypeFilter;
        ThresholdCount = thresholdCount;
        WindowMinutes = windowMinutes;
        Severity = severity;
        Recipients = recipients;
        IsEnabled = isEnabled;
    }

    public string Name { get; private set; } = default!;

    /// <summary>Matched against SecurityEvent.EventType.</summary>
    public string EventTypeFilter { get; private set; } = default!;
    public int ThresholdCount { get; private set; }
    public int WindowMinutes { get; private set; }
    public string Severity { get; private set; } = default!;

    /// <summary>Comma-separated email addresses. Email is the only channel — see NotificationHistory's remarks
    /// on this being the one real notification channel in the codebase.</summary>
    public string Recipients { get; private set; } = default!;
    public bool IsEnabled { get; private set; }

    public void Update(
        string name, string eventTypeFilter, int thresholdCount, int windowMinutes, string severity, string recipients)
    {
        Name = name;
        EventTypeFilter = eventTypeFilter;
        ThresholdCount = thresholdCount;
        WindowMinutes = windowMinutes;
        Severity = severity;
        Recipients = recipients;
    }

    public void SetEnabled(bool isEnabled) => IsEnabled = isEnabled;
}

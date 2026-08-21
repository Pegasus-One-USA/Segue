namespace FHIRBridge.Worker;

/// <summary>Options for the alert-rule evaluation worker role. Enabled by default — cheap, read-mostly queries.</summary>
public sealed class AlertEvaluationOptions
{
    public bool Enabled { get; set; } = true;

    public int IntervalSeconds { get; set; } = 300;
}

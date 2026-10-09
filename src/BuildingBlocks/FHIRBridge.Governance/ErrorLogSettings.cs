namespace FHIRBridge.Governance;

/// <summary>
/// Admin-controlled rules for the error log: which entries are captured, and how long they are kept.
/// Stored as one JSON system setting (<see cref="SettingKey"/>); every host reads it through
/// <see cref="IErrorCapturePolicy"/>.
/// </summary>
public sealed record ErrorLogSettings
{
    public const string SettingKey = "ErrorLog:Settings";
    public const int MinRetentionDays = 1;
    public const int MaxRetentionDays = 3650;
    public const int DefaultRetentionDays = 180;

    public static readonly string[] AllSeverities = ["Critical", "Error", "Warning", "Information", "WorkflowDebug"];

    public static readonly string[] AllCategories = Enum.GetNames<ErrorCategory>();

    /// <summary>How much the WorkflowDebug trace records: <c>Steps</c> = one line per workflow step; <c>Stages</c> adds
    /// the stages inside a step (source fetch per resource type, mapping, transformation, destination writes);
    /// <c>Resources</c> adds a line for individual resources (capped per run).</summary>
    public static readonly string[] AllWorkflowDebugDetails = ["Steps", "Stages", "Resources"];

    /// <summary>Which entry levels are written to the error log. Default: Critical and Error only. Warning and
    /// Information entries come from the application's own log lines and can be numerous - enable with care.</summary>
    public IReadOnlyList<string> CaptureSeverities { get; init; } = ["Critical", "Error"];

    /// <summary>Which error categories are written. Default: all.</summary>
    public IReadOnlyList<string> CaptureCategories { get; init; } = AllCategories;

    /// <summary>When true, entries older than <see cref="RetentionDays"/> are deleted automatically.</summary>
    public bool AutoClearEnabled { get; init; }

    public int RetentionDays { get; init; } = DefaultRetentionDays;

    /// <summary>Only used while the WorkflowDebug entry type is enabled.</summary>
    public string WorkflowDebugDetail { get; init; } = "Steps";

    public static ErrorLogSettings Default { get; } = new();

    /// <summary>Clamps and de-duplicates user input to values this system understands.</summary>
    public ErrorLogSettings Normalize() => this with
    {
        CaptureSeverities = Distinct(CaptureSeverities, AllSeverities),
        CaptureCategories = Distinct(CaptureCategories, AllCategories),
        RetentionDays = Math.Clamp(RetentionDays, MinRetentionDays, MaxRetentionDays),
        WorkflowDebugDetail = AllWorkflowDebugDetails.FirstOrDefault(
            d => string.Equals(d, WorkflowDebugDetail?.Trim(), StringComparison.OrdinalIgnoreCase)) ?? "Steps",
    };

    /// <summary>Is an entry of this severity / category written to the error log?</summary>
    public bool ShouldCapture(string? severity, string? category)
    {
        // "Informational" is the existing internal level for routine, expected outcomes (wrong password, duplicate
        // name...). It is findable by correlation id but never shown as an error, so these settings don't govern it.
        if (string.Equals(severity, "Informational", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var severityName = string.IsNullOrWhiteSpace(severity) ? "Error" : severity;
        if (!CaptureSeverities.Contains(severityName, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        return string.IsNullOrWhiteSpace(category)
            || CaptureCategories.Contains(category, StringComparer.OrdinalIgnoreCase);
    }

    public bool CapturesWarnings => CaptureSeverities.Contains("Warning", StringComparer.OrdinalIgnoreCase);

    /// <summary>Step-by-step workflow trace lines (see <see cref="WorkflowDebug"/>).</summary>
    public bool CapturesWorkflowDebug => CaptureSeverities.Contains("WorkflowDebug", StringComparer.OrdinalIgnoreCase);

    /// <summary>1 = Steps, 2 = Stages, 3 = Resources.</summary>
    public int WorkflowDebugLevel => Array.FindIndex(AllWorkflowDebugDetails, d => string.Equals(d, WorkflowDebugDetail, StringComparison.OrdinalIgnoreCase)) is var i and >= 0 ? i + 1 : 1;

    public bool CapturesInformation => CaptureSeverities.Contains("Information", StringComparer.OrdinalIgnoreCase);

    private static string[] Distinct(IReadOnlyList<string>? input, string[] allowed) =>
        (input ?? [])
            .Select(v => allowed.FirstOrDefault(a => string.Equals(a, v?.Trim(), StringComparison.OrdinalIgnoreCase)))
            .Where(v => v is not null)
            .Select(v => v!)
            .Distinct()
            .ToArray();
}

/// <summary>Synchronous, cheap, always-available view of the current <see cref="ErrorLogSettings"/>. Implementations
/// cache and refresh in the background, so it is safe to call on hot paths (logging, exception capture).</summary>
public interface IErrorCapturePolicy
{
    ErrorLogSettings Current { get; }

    /// <summary>Re-reads the settings now (called after a save so the change is immediate in this process).</summary>
    Task RefreshAsync(CancellationToken cancellationToken = default);
}

/// <summary>Used when no settings store is registered: the built-in defaults.</summary>
public sealed class DefaultErrorCapturePolicy : IErrorCapturePolicy
{
    public ErrorLogSettings Current => ErrorLogSettings.Default;

    public Task RefreshAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}

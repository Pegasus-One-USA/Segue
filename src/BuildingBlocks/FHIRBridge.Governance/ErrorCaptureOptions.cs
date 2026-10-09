namespace FHIRBridge.Governance;

/// <summary>Where a captured error is written.</summary>
public enum ErrorSinkMode
{
    /// <summary>The application's own <c>ErrorLogs</c> table only (default — the pre-existing behaviour).</summary>
    Table = 0,

    /// <summary>Azure Application Insights only: errors get a reference id that is searchable in Application Insights, but are
    /// NOT stored in the ErrorLogs table, so the portal's Errors screens will not list them. Falls back to <see cref="Table"/>
    /// if no connection string is set.</summary>
    ApplicationInsights = 1,

    /// <summary>Both the <c>ErrorLogs</c> table and Application Insights.</summary>
    Both = 2,
}

/// <summary>Binds the <c>ErrorCapture</c> configuration section.</summary>
public sealed class ErrorCaptureOptions
{
    public const string SectionName = "ErrorCapture";

    /// <summary><c>Table</c> | <c>ApplicationInsights</c> | <c>Both</c>.</summary>
    public ErrorSinkMode Sinks { get; set; } = ErrorSinkMode.Table;

    public ApplicationInsightsErrorOptions ApplicationInsights { get; set; } = new();

    /// <summary>Retention floor (HIPAA): no delete path - delete-all, delete-before-date, the settings purge or
    /// auto-clear - may remove an entry newer than this many days. Deployment configuration, deliberately not an
    /// in-app setting, so the people who can delete cannot lower it. 0 switches the floor off.</summary>
    /// <summary>Secret for the keyed hash that turns FHIR resource ids into stable tokens (Patient/#3f9a1c2e). Set the same
    /// value on the Api and Worker. Blank = a random key per process.</summary>
    public string? ResourceIdTokenKey { get; set; }

    public int MinimumRetentionDays { get; set; } = 180;

    public AmbientErrorCaptureOptions Ambient { get; set; } = new();

    public bool WritesToTable => Sinks != ErrorSinkMode.ApplicationInsights;

    public bool WantsApplicationInsights => Sinks != ErrorSinkMode.Table;
}

public sealed class ApplicationInsightsErrorOptions
{
    /// <summary>Application Insights connection string for captured errors. When empty, falls back to
    /// <c>ApplicationInsights:ConnectionString</c>, then <c>Observability:AzureMonitorConnectionString</c>.</summary>
    public string? ConnectionString { get; set; }

    /// <summary>Attach the (scrubbed, summary-only) run timeline to the first error of each correlation id.</summary>
    public bool IncludeCorrelationTimeline { get; set; } = true;
}

/// <summary>Settings for the catch-all capture of errors that no call site routed through the exception manager.</summary>
public sealed class AmbientErrorCaptureOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>Capture <c>AppDomain.UnhandledException</c> and <c>TaskScheduler.UnobservedTaskException</c>.</summary>
    public bool CaptureProcessLevelExceptions { get; set; } = true;

    /// <summary>Capture Error/Fatal log events that carry an exception and were not already captured.</summary>
    public bool CaptureLoggedErrors { get; set; } = true;

    /// <summary>Also capture Error/Fatal log events with no exception (off by default — can be noisy).</summary>
    public bool CaptureLogErrorsWithoutException { get; set; }

    /// <summary>Safety valve for Warning / Information entries (when enabled in the error-log settings): at most this
    /// many are written per minute per process.</summary>
    public int MaxLowSeverityPerMinute { get; set; } = 120;

    /// <summary>How long to wait before treating a logged exception as "not captured by its call site". Call sites
    /// usually log and then call the exception manager a moment later; this avoids double-recording.</summary>
    public int DedupeWindowSeconds { get; set; } = 3;

    /// <summary>Identical errors (type + module + message prefix) inside this window are recorded once.</summary>
    public int RepeatSuppressionSeconds { get; set; } = 30;

    /// <summary>Logger categories never captured ambiently (prevents capture→log→capture loops).</summary>
    public string[] ExcludedCategoryPrefixes { get; set; } =
    [
        "FHIRBridge.Governance",
        "FHIRBridge.Infrastructure.Governance",
    ];
}

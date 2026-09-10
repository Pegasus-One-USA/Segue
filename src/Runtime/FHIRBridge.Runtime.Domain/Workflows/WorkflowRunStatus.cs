namespace FHIRBridge.Runtime.Domain.Workflows;

public enum WorkflowRunStatus
{
    Pending,
    Running,
    Succeeded,
    Failed,
    Cancelled,

    /// <summary>Every node executed, but at least one non-parent resource type on a source node was skipped
    /// because the app isn't authorized for it — everything else in the run still completed and was written.</summary>
    PartialSuccess,

    /// <summary>A source node deferred to an async FHIR Bulk Data <c>$export</c> job instead of blocking for the
    /// job's full duration — the run is paused past that node until <c>BulkExportPollWorker</c> observes the job
    /// complete and resumes execution from the next node.</summary>
    AwaitingBulkExport,

    /// <summary>The caller's parameters passed <c>validate-run</c> and this run is waiting for the <c>/run</c>
    /// call that will execute it. Non-terminal: an attempt that never proceeds (the user was redirected to the
    /// EHR to sign in and never came back) stays here until it is aged out, which is deliberate — that
    /// abandonment was previously invisible.</summary>
    Validated,

    /// <summary>The caller's parameters were rejected by <c>validate-run</c>; nothing was executed and no
    /// outbound call was made. Terminal. Exists as its own status rather than reusing <see cref="Failed"/>
    /// because operationally the two are entirely different: nothing was attempted here.</summary>
    ValidationFailed,

    /// <summary>A <see cref="Validated"/> attempt that was never executed and has now aged out — most often the
    /// user was sent to the EHR to sign in and never came back. Terminal, so it stops being a candidate for
    /// continuation and stops sitting in the list as though something were still pending. Distinct from
    /// <see cref="Cancelled"/>, which is a run that started and was deliberately stopped.</summary>
    Expired
}

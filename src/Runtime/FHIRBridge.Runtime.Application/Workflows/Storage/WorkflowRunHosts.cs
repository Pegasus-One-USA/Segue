using FHIRBridge.Runtime.Domain.Workflows;

namespace FHIRBridge.Runtime.Application.Workflows.Storage;

/// <summary>The process that executes a workflow run.</summary>
public enum WorkflowRunHost
{
    /// <summary>The API: a Run button (sync or async), a checkpoint link, or an EHR / standalone launch callback.</summary>
    Api,

    /// <summary>The Worker: a scheduled run, or any run resumed after its bulk <c>$export</c> completed.</summary>
    Worker,
}

/// <summary>
/// Which host executes a run, worked out from what the run already records: its trigger type, and whether it paused
/// on a bulk export (the Worker's poller resumes those, whoever started them).
/// </summary>
public static class WorkflowRunHosts
{
    /// <summary>Trigger types whose runs execute inside the API process.</summary>
    public static readonly IReadOnlyCollection<string> ApiTriggerTypes = ["Manual", "Checkpoint", "InteractiveLaunch"];

    public static WorkflowRunHost HostOf(WorkflowRun run) =>
        run.BulkRequestId is null && ApiTriggerTypes.Contains(run.TriggerType)
            ? WorkflowRunHost.Api
            : WorkflowRunHost.Worker;
}

namespace FHIRBridge.Application.Abstractions.Pipeline;

/// <summary>
/// Tracks in-flight Configured Pipeline runs (see <see cref="IConfiguredPipelineService.StartAsync"/>) so an
/// operator can request a graceful stop while one is still executing. Singleton, in-memory, and deliberately not
/// persisted — the run's own terminal <c>ConfiguredPipelineRunDto</c>/route-execution rows are the source of truth
/// once it finishes, whether that's a normal completion or a cancellation. Mirrors
/// FHIRBridge.Runtime.Application.Workflows.IWorkflowRunTracker for the Runtime DAG plane.
/// </summary>
public interface IPipelineRunTracker
{
    /// <summary>Registers a tracked in-flight run along with the <see cref="CancellationTokenSource"/> its
    /// execution was started with — the only handle to it, so <see cref="RequestCancellation"/> can signal it
    /// later.</summary>
    void MarkRunning(Guid pipelineRunId, CancellationTokenSource cancellationSource);

    /// <summary>Also disposes the run's <see cref="CancellationTokenSource"/> — safe to call whether or not
    /// cancellation was ever requested.</summary>
    void MarkComplete(Guid pipelineRunId);

    bool IsRunning(Guid pipelineRunId);

    /// <summary>Signals the tracked run's <see cref="CancellationTokenSource"/> so the pipeline stops before its
    /// next resource type/route group starts — the route already in flight is left to finish and reach its
    /// normal terminal status. Returns false if <paramref name="pipelineRunId"/> isn't currently tracked as
    /// running (already completed, or never started as a cancellable run).</summary>
    bool RequestCancellation(Guid pipelineRunId);
}

namespace FHIRBridge.Runtime.Application.Abstractions.Pipeline;

/// <summary>
/// Runtime-plane-local abstraction over the product's real license quota guard
/// (<c>FHIRBridge.Application.Abstractions.Licensing.ILicenseQuotaGuard</c>). This project deliberately does
/// not reference the main <c>FHIRBridge.Application</c> project — the Runtime plane's own Domain/Application/
/// Infrastructure stack is fully separate (see this solution's Clean Architecture layering) — so
/// <c>PipelineOrchestrator</c>/<c>InMemoryWorkflowDefinitionStore</c> (both Runtime.Application) depend on this
/// small interface instead, and <c>FHIRBridge.Runtime.Infrastructure</c> (which DOES reference the main
/// Application project) supplies the real implementation as a thin forwarding adapter. This keeps both gates
/// real without crossing the Runtime.Application → Runtime.Infrastructure direction NetArchTest forbids.
/// </summary>
public interface IPipelineRunLicenseGuard
{
    /// <summary>Throws when a new pipeline run may not start — a truly expired license, or the monthly
    /// processed-records cap already at/over its limit. Call ONLY at a run's trigger point, never mid-run: an
    /// in-flight run must never be interrupted by this check.</summary>
    Task EnsureCanStartNewRunAsync(CancellationToken cancellationToken);

    /// <summary>Throws when the combined (Configured-Pipeline-routes + Runtime-workflow-definitions) workflow
    /// count is already at/over the license's cap. Used by <c>InMemoryWorkflowDefinitionStore</c> — the no-DB
    /// dev/test fallback store, which bypasses EF entirely and so is never seen by the EF-backed
    /// SaveChangesInterceptor that enforces this for the real store — only on a genuinely new workflow id,
    /// never an edit of an existing one.</summary>
    Task EnsureWorkflowQuotaAvailableAsync(CancellationToken cancellationToken);
}

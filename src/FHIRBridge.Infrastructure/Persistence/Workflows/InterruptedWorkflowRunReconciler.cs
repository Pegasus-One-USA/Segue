using FHIRBridge.Runtime.Application.Workflows.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FHIRBridge.Infrastructure.Persistence.Workflows;

/// <summary>
/// Closes workflow runs left at "Running" by a host that stopped before they finished.
/// <para>A run executes inside one process (see <see cref="WorkflowRunHosts"/>) and nothing else ever finishes it.
/// When that process stops (a restart, a shutdown, a crash) the row survives with nothing alive to complete it: it
/// shows "Running" for ever, and Cancel refuses it because no live run has that id. The same happens when the run's
/// own outcome cannot be saved.</para>
/// <para>At start-up nothing of this host's can be running yet, so every such run started before this process is dead
/// by definition. Each host sweeps only its own runs, so restarting the API never fails a run the Worker is still
/// executing, and the other way round. Runs are failed with a reason Execution History shows, never restarted:
/// re-running is the user's call.</para>
/// <para>Modeled on <see cref="Terminology.TerminologyImportOrphanReconciler"/>.</para>
/// </summary>
public sealed class InterruptedWorkflowRunReconciler : IHostedService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly WorkflowRunHost _host;
    private readonly ILogger<InterruptedWorkflowRunReconciler> _logger;
    private readonly DateTimeOffset _hostStartedAtUtc = DateTimeOffset.UtcNow;

    public InterruptedWorkflowRunReconciler(
        IServiceScopeFactory scopeFactory,
        WorkflowRunHost host,
        ILogger<InterruptedWorkflowRunReconciler> logger)
    {
        _scopeFactory = scopeFactory;
        _host = host;
        _logger = logger;
    }

    /// <summary>The error Execution History shows on a run this host never finished.</summary>
    public static string ReasonFor(WorkflowRunHost host) =>
        $"Interrupted: the {(host == WorkflowRunHost.Api ? "API" : "Worker")} stopped (restart, shutdown or crash) " +
        "before this run finished, so it did not complete. Nothing after the last completed step was done. Run it again.";

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var runStore = scope.ServiceProvider.GetRequiredService<IWorkflowRunStore>();

            var interrupted = await runStore.FailInterruptedRunsAsync(_host, _hostStartedAtUtc, ReasonFor(_host), cancellationToken);
            if (interrupted > 0)
            {
                _logger.LogWarning(
                    "Marked {Count} workflow run(s) as failed: the {Host} stopped before they finished.",
                    interrupted, _host);
            }
        }
        catch (Exception exception)
        {
            // Never block start-up over run bookkeeping: a failure leaves the stale rows exactly as they were.
            _logger.LogError(exception, "Failed to close interrupted workflow runs at start-up.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

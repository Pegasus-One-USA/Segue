using FHIRBridge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FHIRBridge.Infrastructure.Terminology;

/// <summary>
/// Closes out import-history rows left stranded at "Running" by a host that stopped mid-import.
///
/// Terminology imports run strictly in-process, on <see cref="TerminologyImportBackgroundService"/>'s drain
/// loop (see <see cref="TerminologyImportChannel"/> for why they are deliberately not routed through the
/// Worker). The history row is committed as "Running" before the work starts, so if the process is killed
/// — a redeploy, a crash, a developer restarting the API — the in-memory job dies while that row survives in
/// the database with nothing left alive to finish it. Nothing reconciled those rows, so they stayed "Running"
/// forever.
///
/// That is not merely cosmetic. The portal's auto-sync skips any system it believes is already running, so a
/// single stranded row permanently prevents that code system from ever syncing again — it silently stops
/// updating while the screen shows a spinner for work that ended days ago.
///
/// Because imports are in-process only, "Running" at startup is unambiguous: no import can have survived the
/// restart that just happened, so every such row is dead by definition. That is why this deliberately does
/// NOT record or check a process id — a pid would be meaningless across a container restart or a host move,
/// where it may well have been reassigned to an unrelated process.
///
/// These rows are marked "Interrupted", not "Failed", and are NOT restarted automatically. Auto-resuming on
/// boot is tempting but hazardous for imports this large: if a host is crash-looping *because* of one of them
/// (an out-of-memory during an 80k-concept load, say), re-enqueuing it every startup turns a single bad
/// import into a cycle the host never escapes. Recovery is therefore driven by the portal's own scan-and-sync
/// pass, or by the system's next scheduled run.
/// </summary>
public sealed class TerminologyImportOrphanReconciler : IHostedService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<TerminologyImportOrphanReconciler> _logger;

    public TerminologyImportOrphanReconciler(
        IServiceScopeFactory scopeFactory, ILogger<TerminologyImportOrphanReconciler> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FHIRBridgeDbContext>();

            var orphans = await db.HapiTerminologyImportHistory
                .Where(x => x.Status == "Running")
                .ToListAsync(cancellationToken);

            if (orphans.Count == 0)
            {
                return;
            }

            foreach (var orphan in orphans)
            {
                orphan.MarkInterrupted();
            }

            await db.SaveChangesAsync(cancellationToken);
            _logger.LogWarning(
                "Marked {Count} terminology import(s) as interrupted; they were left Running by a previous host shutdown: {CodeSystems}.",
                orphans.Count, string.Join(", ", orphans.Select(o => o.CodeSystem)));
        }
        catch (Exception exception)
        {
            // Never block startup over history bookkeeping: a failure here leaves a stale row, which is
            // exactly the state we were already in, whereas throwing would take the whole host down.
            _logger.LogError(exception, "Failed to reconcile interrupted terminology imports at startup.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

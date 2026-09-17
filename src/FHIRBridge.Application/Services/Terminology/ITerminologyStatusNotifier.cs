namespace FHIRBridge.Application.Services.Terminology;

/// <summary>
/// Best-effort live push of a HAPI-terminology sync's status transition, so the Terminology Server table
/// (Settings → System Settings → General) reflects a running import as it happens instead of polling for it.
///
/// Optional, and injected as nullable for the same reason <see cref="Runtime"/>'s own
/// <c>IRunStatusNotifier</c> is: only the API host registers an implementation, because only it owns a
/// SignalR hub to push into. The Worker process — which is where the SCHEDULED syncs run (see
/// <c>src/Worker/*HapiTerminologySyncWorker.cs</c>) — resolves this as null and simply skips the push. Those
/// syncs still record their history rows normally; a client finds out about them on its next load rather
/// than live. Wiring the Worker up would mean pushing across process boundaries, which is deliberately out
/// of scope here.
/// </summary>
public interface ITerminologyStatusNotifier
{
    Task NotifyAsync(TerminologyStatusChangedEvent statusEvent, CancellationToken cancellationToken);
}

/// <summary>
/// One sync's transition into a new state.
/// </summary>
/// <param name="Code">The code system's registry code (e.g. "LOINC") — what the client keys its row by.</param>
/// <param name="Status">"Running" | "Succeeded" | "Failed", mirroring the history row's own status string.</param>
/// <param name="ImportedConceptCount">Concepts imported, on a successful run only.</param>
/// <param name="Version">The version that was imported, when the source reported one.</param>
/// <param name="ErrorMessage">Why the run failed — null on every other status.</param>
public sealed record TerminologyStatusChangedEvent(
    string Code,
    string Status,
    DateTimeOffset OccurredAt,
    int? ImportedConceptCount = null,
    string? Version = null,
    string? ErrorMessage = null);

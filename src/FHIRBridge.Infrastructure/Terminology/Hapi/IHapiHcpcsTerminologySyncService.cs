namespace FHIRBridge.Infrastructure.Terminology.Hapi;

/// <summary>
/// Downloads the real, official CMS HCPCS Level II code file (public, no credentials) and loads it
/// into the embedded HAPI FHIR terminology server. Mirrors <see cref="IHapiCvxTerminologySyncService"/>.
/// </summary>
public interface IHapiHcpcsTerminologySyncService
{
    Task<HapiHcpcsSyncResult> SyncAsync(CancellationToken cancellationToken);
}

/// <summary>
/// <paramref name="Version"/> is the CMS quarterly zip filename actually synced — the same value
/// <see cref="IHapiVersionCheckable.GetLatestAvailableVersionAsync"/> reports, so the two are
/// directly comparable. It must be carried back to the import-history row: the "new version
/// available" banner compares the last succeeded history Version against the latest available one,
/// so recording null there leaves the banner showing forever no matter how often the sync succeeds.
/// </summary>
public sealed record HapiHcpcsSyncResult(int TotalConceptCount, TimeSpan Duration, string Version);

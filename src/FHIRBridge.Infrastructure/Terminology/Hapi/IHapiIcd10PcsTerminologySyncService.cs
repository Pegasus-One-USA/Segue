namespace FHIRBridge.Infrastructure.Terminology.Hapi;

/// <summary>
/// Downloads the real, official CMS ICD-10-PCS order file (public, no credentials) and loads it
/// into the embedded HAPI FHIR terminology server. Mirrors <see cref="IHapiIcd10TerminologySyncService"/>.
/// </summary>
public interface IHapiIcd10PcsTerminologySyncService
{
    Task<HapiIcd10PcsSyncResult> SyncAsync(CancellationToken cancellationToken);
}

/// <summary><paramref name="Version"/> is the CMS fiscal-year zip filename actually synced — the same
/// value the version check reports, so the two compare directly. It must reach the import-history row or
/// the "new version available" banner never clears. See <see cref="HapiHcpcsSyncResult"/>.</summary>
public sealed record HapiIcd10PcsSyncResult(int TotalConceptCount, int BillableConceptCount, TimeSpan Duration, string Version);

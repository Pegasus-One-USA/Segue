namespace FHIRBridge.Infrastructure.Terminology.Hapi;

/// <summary>
/// Downloads the real, official SNOMED CT (US Edition) release (via the existing
/// <c>IUtsReleaseClient</c> — same UMLS/UTS API key used by <c>SnomedSynchronizationService</c>)
/// and loads it into the embedded HAPI FHIR terminology server. Mirrors
/// <see cref="IHapiRxNormTerminologySyncService"/>.
/// </summary>
public interface IHapiSnomedTerminologySyncService
{
    Task<HapiSnomedSyncResult> SyncAsync(CancellationToken cancellationToken);
}

public sealed record HapiSnomedSyncResult(string Version, int TotalConceptCount, TimeSpan Duration);

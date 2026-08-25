namespace FHIRBridge.Infrastructure.Terminology.Hapi;

/// <summary>
/// Downloads the real, official RxNorm release (via the existing <c>IUtsReleaseClient</c> — same
/// UMLS/UTS API key used by <c>RxNormSynchronizationService</c>) and loads it into the embedded
/// HAPI FHIR terminology server. Mirrors <see cref="IHapiLoincTerminologySyncService"/>.
/// </summary>
public interface IHapiRxNormTerminologySyncService
{
    Task<HapiRxNormSyncResult> SyncAsync(CancellationToken cancellationToken);
}

public sealed record HapiRxNormSyncResult(string? Version, int TotalConceptCount, TimeSpan Duration);

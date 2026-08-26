namespace FHIRBridge.Infrastructure.Terminology.Hapi;

/// <summary>
/// Downloads the official, credentialed LOINC release (via the existing <c>ILoincReleaseClient</c>
/// — same LOINC account used by <c>LoincSynchronizationService</c>) and loads it into the embedded
/// HAPI FHIR terminology server. Mirrors <see cref="IHapiCvxTerminologySyncService"/>.
/// </summary>
public interface IHapiLoincTerminologySyncService
{
    Task<HapiLoincSyncResult> SyncAsync(CancellationToken cancellationToken);
}

public sealed record HapiLoincSyncResult(string Version, int TotalConceptCount, TimeSpan Duration);

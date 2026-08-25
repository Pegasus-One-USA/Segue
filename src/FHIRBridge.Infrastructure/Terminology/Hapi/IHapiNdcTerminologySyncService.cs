namespace FHIRBridge.Infrastructure.Terminology.Hapi;

/// <summary>
/// Downloads the real, official FDA National Drug Code (NDC) directory (public, no credentials) and
/// loads it into the embedded HAPI FHIR terminology server. Mirrors <see cref="IHapiCvxTerminologySyncService"/>.
/// </summary>
public interface IHapiNdcTerminologySyncService
{
    Task<HapiNdcSyncResult> SyncAsync(CancellationToken cancellationToken);
}

public sealed record HapiNdcSyncResult(int TotalConceptCount, TimeSpan Duration);

namespace FHIRBridge.Infrastructure.Terminology.Hapi;

/// <summary>
/// Downloads the real, official CDC CVX vaccine code table (public, no credentials) and loads it
/// into the embedded HAPI FHIR terminology server. Mirrors <see cref="IHapiIcd10TerminologySyncService"/>.
/// </summary>
public interface IHapiCvxTerminologySyncService
{
    Task<HapiCvxSyncResult> SyncAsync(CancellationToken cancellationToken);
}

public sealed record HapiCvxSyncResult(int TotalConceptCount, int ActiveConceptCount, TimeSpan Duration);

namespace FHIRBridge.Infrastructure.Terminology.Hapi;

/// <summary>
/// Downloads the official UCUM (Unified Code for Units of Measure) specification XML (public, no
/// credentials) and loads it into the embedded HAPI FHIR terminology server. Mirrors
/// <see cref="IHapiCvxTerminologySyncService"/>.
/// </summary>
public interface IHapiUcumTerminologySyncService
{
    Task<HapiUcumSyncResult> SyncAsync(CancellationToken cancellationToken);
}

public sealed record HapiUcumSyncResult(int TotalConceptCount, TimeSpan Duration);

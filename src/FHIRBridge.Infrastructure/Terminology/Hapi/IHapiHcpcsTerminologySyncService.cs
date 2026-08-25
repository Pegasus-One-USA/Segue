namespace FHIRBridge.Infrastructure.Terminology.Hapi;

/// <summary>
/// Downloads the real, official CMS HCPCS Level II code file (public, no credentials) and loads it
/// into the embedded HAPI FHIR terminology server. Mirrors <see cref="IHapiCvxTerminologySyncService"/>.
/// </summary>
public interface IHapiHcpcsTerminologySyncService
{
    Task<HapiHcpcsSyncResult> SyncAsync(CancellationToken cancellationToken);
}

public sealed record HapiHcpcsSyncResult(int TotalConceptCount, TimeSpan Duration);

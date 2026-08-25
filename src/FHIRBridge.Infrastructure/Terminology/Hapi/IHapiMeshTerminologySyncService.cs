namespace FHIRBridge.Infrastructure.Terminology.Hapi;

/// <summary>
/// Downloads the real, official NLM MeSH descriptor file (public, no credentials — NLM publishes it
/// as unrestricted linked open data) and loads it into the embedded HAPI FHIR terminology server.
/// Mirrors <see cref="IHapiCvxTerminologySyncService"/>.
/// </summary>
public interface IHapiMeshTerminologySyncService
{
    Task<HapiMeshSyncResult> SyncAsync(CancellationToken cancellationToken);
}

public sealed record HapiMeshSyncResult(int TotalConceptCount, TimeSpan Duration);

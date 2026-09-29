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

/// <summary><paramref name="Version"/> is the MeSH release year actually synced — the same value the
/// version check reports, so the two compare directly. It must reach the import-history row or the
/// "new version available" banner never clears. See <see cref="HapiHcpcsSyncResult"/>.</summary>
public sealed record HapiMeshSyncResult(int TotalConceptCount, TimeSpan Duration, string Version);

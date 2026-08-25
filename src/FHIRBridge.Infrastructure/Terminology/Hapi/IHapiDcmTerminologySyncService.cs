namespace FHIRBridge.Infrastructure.Terminology.Hapi;

/// <summary>
/// Downloads the official DICOM Controlled Terminology (DCM) ontology (public, no credentials —
/// NEMA publishes it as an open OWL/RDF file) and loads it into the embedded HAPI FHIR terminology
/// server. Mirrors <see cref="IHapiCvxTerminologySyncService"/>.
/// </summary>
public interface IHapiDcmTerminologySyncService
{
    Task<HapiDcmSyncResult> SyncAsync(CancellationToken cancellationToken);
}

public sealed record HapiDcmSyncResult(int TotalConceptCount, TimeSpan Duration);

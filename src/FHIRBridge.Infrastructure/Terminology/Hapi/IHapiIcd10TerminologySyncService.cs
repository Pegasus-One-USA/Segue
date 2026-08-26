namespace FHIRBridge.Infrastructure.Terminology.Hapi;

/// <summary>
/// Downloads the official, public CMS/CDC ICD-10-CM release and loads it into the embedded HAPI
/// FHIR terminology server (see docker-compose.yml's hapi-terminology/hapi-terminology-postgres
/// services) — no credentials required for this code system. This is new, standalone
/// infrastructure for the terminology-server migration POC; it does not modify or replace the
/// existing <c>Icd10ImportService</c>/<c>Icd10ConfigurationController</c> local-database path.
/// </summary>
public interface IHapiIcd10TerminologySyncService
{
    Task<HapiIcd10SyncResult> SyncAsync(CancellationToken cancellationToken);
}

public sealed record HapiIcd10SyncResult(
    string ReleaseYear,
    int TotalConceptCount,
    int BillableConceptCount,
    TimeSpan Duration);

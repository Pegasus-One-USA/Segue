namespace FHIRBridge.Infrastructure.Terminology.Hapi;

/// <summary>
/// Downloads the official WHO ICD-11 MMS (Mortality and Morbidity Statistics) linearization export
/// (public, no credentials — WHO's bulk download area is open; only the interactive entity-browsing
/// API needs an OAuth2 client) and loads it into the embedded HAPI FHIR terminology server. Mirrors
/// <see cref="IHapiCvxTerminologySyncService"/>.
/// </summary>
public interface IHapiIcd11TerminologySyncService
{
    Task<HapiIcd11SyncResult> SyncAsync(CancellationToken cancellationToken);
}

public sealed record HapiIcd11SyncResult(int TotalConceptCount, TimeSpan Duration);

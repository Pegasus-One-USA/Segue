namespace FHIRBridge.Infrastructure.Terminology.Hapi;

/// <summary>
/// Downloads the official ICPC-3 (International Classification of Primary Care, 3rd Revision)
/// dataset (public, no credentials — WONCA made ICPC-3 openly CC-licensed, unlike the older ICPC-2
/// which requires a paid commercial license) and loads it into the embedded HAPI FHIR terminology
/// server. Mirrors <see cref="IHapiCvxTerminologySyncService"/>.
/// </summary>
public interface IHapiIcpc3TerminologySyncService
{
    Task<HapiIcpc3SyncResult> SyncAsync(CancellationToken cancellationToken);
}

public sealed record HapiIcpc3SyncResult(int TotalConceptCount, TimeSpan Duration);

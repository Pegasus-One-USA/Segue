namespace FHIRBridge.Application.Abstractions.Sources;

/// <summary>
/// Idempotently imports one EHR vendor's public FHIR endpoint directory (e.g. Epic's
/// https://open.epic.com/Endpoints/R4) into the shared EhrEndpoints table at startup. Each vendor gets its own
/// implementation (see EpicEndpointDirectorySeeder); all of them are resolved as a collection and run at boot, so
/// adding a new vendor's directory is "register one more class," never a change to existing seeders or callers.
/// Implementations must scope their "already seeded" check to their own vendor's rows — the table is shared.
/// </summary>
public interface IEhrEndpointDirectorySeeder
{
    /// <summary>
    /// Fetches and inserts any directory entries for this seeder's vendor not already present (matched by the
    /// vendor-assigned endpoint id). No-ops if this vendor already has rows. Safe to call on every boot.
    /// </summary>
    Task EnsureAsync(CancellationToken cancellationToken);
}

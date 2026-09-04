namespace FHIRBridge.Infrastructure.Terminology.Hapi;

/// <summary>
/// Optional capability: a Hapi*TerminologySyncService whose source publishes a discoverable version
/// identifier without needing a full download can implement this to back the terminology settings
/// screen's "check for updates" scan. Not every source has one — CVX/UCUM/ICPC-3/DCM/NDC are undated
/// "current snapshot" endpoints with no version concept at all, and ICD-10-CM/ICD-11 MMS use a source
/// URL/year fixed in code rather than a discoverable "latest" pointer — those sync services simply
/// don't implement this interface, and the registry/UI treat that as "not supported," not an error.
/// </summary>
public interface IHapiVersionCheckable
{
    /// <summary>The latest version/release identifier currently published by the source, without
    /// downloading the full release — null if it genuinely cannot be determined right now (e.g. a
    /// transient network failure fetching just the release metadata/listing page), which the caller
    /// should treat the same as "no update detected" rather than surfacing an error for what's meant
    /// to be a lightweight, frequent background check.</summary>
    Task<string?> GetLatestAvailableVersionAsync(CancellationToken cancellationToken);
}

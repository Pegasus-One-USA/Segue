namespace FHIRBridge.Domain.Entities.Terminology;

/// <summary>
/// One row per manual "Run Now" attempt against a HAPI-terminology-server sync (see
/// Infrastructure/Terminology/Hapi/HapiTerminologySystemRegistry). Shared across all 13 code systems via
/// <see cref="CodeSystem"/> rather than one table per system, since the shape is identical. Scheduled
/// worker runs do not write here today — only manual runs triggered from the portal.
/// </summary>
public sealed class HapiTerminologyImportHistory
{
    private HapiTerminologyImportHistory() { }
    public Guid Id { get; private set; } = Guid.NewGuid();
    public string CodeSystem { get; private set; } = default!;
    public string? Version { get; private set; }
    public DateTime StartedOnUtc { get; private set; }
    public DateTime? CompletedOnUtc { get; private set; }
    public int ImportedConceptCount { get; private set; }
    public string Status { get; private set; } = default!;
    public string? ErrorMessage { get; private set; }

    public HapiTerminologyImportHistory(string codeSystem)
    {
        Id = Guid.NewGuid(); CodeSystem = codeSystem; Status = "Running"; StartedOnUtc = DateTime.UtcNow;
    }

    /// <summary>Version is persisted as varchar(32), but the strings handed in here come from the code
    /// systems' own publishers and are not length-controlled — CMS, for instance, versions HCPCS by its
    /// release filename ("october-2026-alpha-numeric-hcpcs-file.zip", 41 characters). An overlong value used
    /// to surface as a 22001 truncation error thrown from SaveChangesAsync *after* the import had already
    /// written every concept, which flipped a genuinely successful run to Failed. Truncating is the right
    /// trade here: this column is a human-readable provenance note shown in the History dialog, never
    /// matched on, so a clipped value costs nothing while a lost import costs an entire re-download.</summary>
    public const int MaxVersionLength = 32;

    /// <summary>Clips a publisher's version string to what <see cref="Version"/> can actually hold.
    /// Public because comparing a STORED version against a freshly fetched one has to put both through the
    /// same rule: the stored side has already been clipped, so comparing it raw against a 41-character
    /// upstream filename never matches, <c>updateAvailable</c> is permanently true, and the portal
    /// re-downloads and re-imports the whole code system on every single page load.</summary>
    public static string? ClipVersion(string? version) =>
        version is { Length: > MaxVersionLength } ? version[..MaxVersionLength] : version;

    public void Complete(int count, string? version)
    {
        ImportedConceptCount = count;
        Version = ClipVersion(version);
        Status = "Succeeded";
        CompletedOnUtc = DateTime.UtcNow;
    }

    public void Fail(string error) { Status = "Failed"; ErrorMessage = error; CompletedOnUtc = DateTime.UtcNow; }

    /// <summary>Closes out a run that never finished because the host stopped underneath it. Deliberately a
    /// status of its own rather than reusing <see cref="Fail"/>: the import did not fail on its merits — it
    /// was never given the chance to finish — and conflating the two hides a restart problem inside what
    /// looks like a data-source problem. Keeping them apart also leaves the door open to retrying
    /// interrupted runs automatically one day, which must never happen for a genuine failure such as a 404
    /// from the publisher.</summary>
    public void MarkInterrupted()
    {
        Status = "Interrupted";
        ErrorMessage = "The host stopped while this import was running, so it never completed. Run it again to retry.";
        CompletedOnUtc = DateTime.UtcNow;
    }
}

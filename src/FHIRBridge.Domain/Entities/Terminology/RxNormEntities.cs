namespace FHIRBridge.Domain.Entities.Terminology;

/// <summary>One RxNorm concept (RXNCUI), keyed by its RXCUI. Name/TermType come from the RXNORM-source
/// atom RxNav's own release designates as canonical for that concept (see RxNormImportService).</summary>
public sealed class RxNormConcept
{
    private RxNormConcept() { }

    public RxNormConcept(string rxcui, string name, string? termType, bool isActive, string version)
    {
        Rxcui = rxcui.Trim();
        Name = name.Trim();
        TermType = termType?.Trim();
        IsActive = isActive;
        Version = version.Trim();
        CreatedOnUtc = DateTime.UtcNow;
        ModifiedOnUtc = DateTime.UtcNow;
    }

    public string Rxcui { get; private set; } = default!;
    public string Name { get; private set; } = default!;
    public string? TermType { get; private set; }
    public bool IsActive { get; private set; }
    public string Version { get; private set; } = default!;
    public DateTime CreatedOnUtc { get; private set; }
    public DateTime ModifiedOnUtc { get; private set; }
}

public sealed class RxNormVersion
{
    private RxNormVersion() { }
    public Guid Id { get; private set; } = Guid.NewGuid();
    public string Version { get; private set; } = default!;
    public DateTime? ReleaseDateUtc { get; private set; }
    public string? ChecksumSha256 { get; private set; }
    public bool IsActive { get; private set; }
    public DateTime ImportedOnUtc { get; private set; }

    public RxNormVersion(string version, DateTime? releaseDateUtc, string? checksumSha256, bool isActive)
    {
        Id = Guid.NewGuid(); Version = version; ReleaseDateUtc = releaseDateUtc; ChecksumSha256 = checksumSha256;
        IsActive = isActive; ImportedOnUtc = DateTime.UtcNow;
    }
    public void SetActive(bool isActive) => IsActive = isActive;
}

public sealed class RxNormImportHistory
{
    private RxNormImportHistory() { }
    public Guid Id { get; private set; } = Guid.NewGuid();
    public string? Version { get; private set; }
    public DateTime StartedOnUtc { get; private set; }
    public DateTime? CompletedOnUtc { get; private set; }
    public int ImportedConceptCount { get; private set; }
    public string? ChecksumSha256 { get; private set; }
    public string Status { get; private set; } = default!;
    public string? ErrorMessage { get; private set; }

    public RxNormImportHistory(string? version, string? checksumSha256)
    {
        Id = Guid.NewGuid(); Version = version; ChecksumSha256 = checksumSha256; Status = "Running"; StartedOnUtc = DateTime.UtcNow;
    }
    public void SetVersion(string version) => Version = version;
    public void Complete(int count) { ImportedConceptCount = count; Status = "Succeeded"; CompletedOnUtc = DateTime.UtcNow; }
    public void Fail(string error) { Status = "Failed"; ErrorMessage = error; CompletedOnUtc = DateTime.UtcNow; }
}

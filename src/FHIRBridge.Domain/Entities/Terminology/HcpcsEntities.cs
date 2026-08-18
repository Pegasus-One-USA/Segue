namespace FHIRBridge.Domain.Entities.Terminology;

/// <summary>One HCPCS Level II code, sourced from CMS's quarterly/annual alpha-numeric release.</summary>
public sealed class HcpcsCode
{
    private HcpcsCode() { }

    public HcpcsCode(string code, string shortDescription, string longDescription, bool isActive, string version)
    {
        Code = code.Trim();
        ShortDescription = shortDescription.Trim();
        LongDescription = longDescription.Trim();
        IsActive = isActive;
        Version = version.Trim();
        CreatedOnUtc = DateTime.UtcNow;
        ModifiedOnUtc = DateTime.UtcNow;
    }

    public string Code { get; private set; } = default!;
    public string ShortDescription { get; private set; } = default!;
    public string LongDescription { get; private set; } = default!;
    public bool IsActive { get; private set; }
    public string Version { get; private set; } = default!;
    public DateTime CreatedOnUtc { get; private set; }
    public DateTime ModifiedOnUtc { get; private set; }

    public void Update(string shortDescription, string longDescription, string version, bool isActive)
    {
        ShortDescription = shortDescription.Trim();
        LongDescription = longDescription.Trim();
        Version = version.Trim();
        IsActive = isActive;
        ModifiedOnUtc = DateTime.UtcNow;
    }
}

public sealed class HcpcsVersion
{
    private HcpcsVersion() { }
    public Guid Id { get; private set; } = Guid.NewGuid();
    public string Version { get; private set; } = default!;
    public DateTime? ReleaseDateUtc { get; private set; }
    public string? ChecksumSha256 { get; private set; }
    public bool IsActive { get; private set; }
    public DateTime ImportedOnUtc { get; private set; }

    public HcpcsVersion(string version, DateTime? releaseDateUtc, string? checksumSha256, bool isActive)
    {
        Id = Guid.NewGuid(); Version = version; ReleaseDateUtc = releaseDateUtc; ChecksumSha256 = checksumSha256;
        IsActive = isActive; ImportedOnUtc = DateTime.UtcNow;
    }
    public void SetActive(bool isActive) => IsActive = isActive;
}

public sealed class HcpcsImportHistory
{
    private HcpcsImportHistory() { }
    public Guid Id { get; private set; } = Guid.NewGuid();
    public string? Version { get; private set; }
    public DateTime StartedOnUtc { get; private set; }
    public DateTime? CompletedOnUtc { get; private set; }
    public int ImportedConceptCount { get; private set; }
    public string? ChecksumSha256 { get; private set; }
    public string Status { get; private set; } = default!;
    public string? ErrorMessage { get; private set; }

    public HcpcsImportHistory(string? version, string? checksumSha256)
    {
        Id = Guid.NewGuid(); Version = version; ChecksumSha256 = checksumSha256; Status = "Running"; StartedOnUtc = DateTime.UtcNow;
    }
    public void SetVersion(string version) => Version = version;
    public void Complete(int count) { ImportedConceptCount = count; Status = "Succeeded"; CompletedOnUtc = DateTime.UtcNow; }
    public void Fail(string error) { Status = "Failed"; ErrorMessage = error; CompletedOnUtc = DateTime.UtcNow; }
}

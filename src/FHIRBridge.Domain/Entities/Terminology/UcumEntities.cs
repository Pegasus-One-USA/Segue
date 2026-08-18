namespace FHIRBridge.Domain.Entities.Terminology;

/// <summary>One UCUM unit definition, sourced from the ucum-org/ucum GitHub repository's XML release.</summary>
public sealed class UcumUnit
{
    private UcumUnit() { }

    public UcumUnit(string code, string name, string? printSymbol, bool isActive, string version)
    {
        Code = code.Trim();
        Name = name.Trim();
        PrintSymbol = printSymbol?.Trim();
        IsActive = isActive;
        Version = version.Trim();
        CreatedOnUtc = DateTime.UtcNow;
        ModifiedOnUtc = DateTime.UtcNow;
    }

    public string Code { get; private set; } = default!;
    public string Name { get; private set; } = default!;
    public string? PrintSymbol { get; private set; }
    public bool IsActive { get; private set; }
    public string Version { get; private set; } = default!;
    public DateTime CreatedOnUtc { get; private set; }
    public DateTime ModifiedOnUtc { get; private set; }

    public void Update(string name, string? printSymbol, string version, bool isActive)
    {
        Name = name.Trim();
        PrintSymbol = printSymbol?.Trim();
        Version = version.Trim();
        IsActive = isActive;
        ModifiedOnUtc = DateTime.UtcNow;
    }
}

public sealed class UcumVersion
{
    private UcumVersion() { }
    public Guid Id { get; private set; } = Guid.NewGuid();
    public string Version { get; private set; } = default!;
    public DateTime? ReleaseDateUtc { get; private set; }
    public string? ChecksumSha256 { get; private set; }
    public bool IsActive { get; private set; }
    public DateTime ImportedOnUtc { get; private set; }

    public UcumVersion(string version, DateTime? releaseDateUtc, string? checksumSha256, bool isActive)
    {
        Id = Guid.NewGuid(); Version = version; ReleaseDateUtc = releaseDateUtc; ChecksumSha256 = checksumSha256;
        IsActive = isActive; ImportedOnUtc = DateTime.UtcNow;
    }
    public void SetActive(bool isActive) => IsActive = isActive;
}

public sealed class UcumImportHistory
{
    private UcumImportHistory() { }
    public Guid Id { get; private set; } = Guid.NewGuid();
    public string? Version { get; private set; }
    public DateTime StartedOnUtc { get; private set; }
    public DateTime? CompletedOnUtc { get; private set; }
    public int ImportedConceptCount { get; private set; }
    public string? ChecksumSha256 { get; private set; }
    public string Status { get; private set; } = default!;
    public string? ErrorMessage { get; private set; }

    public UcumImportHistory(string? version, string? checksumSha256)
    {
        Id = Guid.NewGuid(); Version = version; ChecksumSha256 = checksumSha256; Status = "Running"; StartedOnUtc = DateTime.UtcNow;
    }
    public void SetVersion(string version) => Version = version;
    public void Complete(int count) { ImportedConceptCount = count; Status = "Succeeded"; CompletedOnUtc = DateTime.UtcNow; }
    public void Fail(string error) { Status = "Failed"; ErrorMessage = error; CompletedOnUtc = DateTime.UtcNow; }
}

namespace FHIRBridge.Domain.Entities.Terminology;

/// <summary>One ICD-10-CM code from the CMS annual "order file" release, keyed by its code.</summary>
public sealed class Icd10Code
{
    private Icd10Code() { }

    public Icd10Code(string code, int orderNumber, bool isBillable, string shortDescription, string longDescription, string version)
    {
        Code = code.Trim();
        OrderNumber = orderNumber;
        IsBillable = isBillable;
        ShortDescription = shortDescription.Trim();
        LongDescription = longDescription.Trim();
        Version = version.Trim();
        IsActive = true;
        CreatedOnUtc = DateTime.UtcNow;
        ModifiedOnUtc = DateTime.UtcNow;
    }

    public string Code { get; private set; } = default!;
    public int OrderNumber { get; private set; }
    public bool IsBillable { get; private set; }
    public string ShortDescription { get; private set; } = default!;
    public string LongDescription { get; private set; } = default!;
    public string Version { get; private set; } = default!;
    public bool IsActive { get; private set; }
    public DateTime CreatedOnUtc { get; private set; }
    public DateTime ModifiedOnUtc { get; private set; }
}

public sealed class Icd10Version
{
    private Icd10Version() { }
    public Guid Id { get; private set; } = Guid.NewGuid();
    public string Version { get; private set; } = default!;
    public DateTime? ReleaseDateUtc { get; private set; }
    public string? ChecksumSha256 { get; private set; }
    public bool IsActive { get; private set; }
    public DateTime ImportedOnUtc { get; private set; }

    public Icd10Version(string version, DateTime? releaseDateUtc, string? checksumSha256, bool isActive)
    {
        Id = Guid.NewGuid(); Version = version; ReleaseDateUtc = releaseDateUtc; ChecksumSha256 = checksumSha256;
        IsActive = isActive; ImportedOnUtc = DateTime.UtcNow;
    }
    public void SetActive(bool isActive) => IsActive = isActive;
}

public sealed class Icd10ImportHistory
{
    private Icd10ImportHistory() { }
    public Guid Id { get; private set; } = Guid.NewGuid();
    public string? Version { get; private set; }
    public DateTime StartedOnUtc { get; private set; }
    public DateTime? CompletedOnUtc { get; private set; }
    public int ImportedCodeCount { get; private set; }
    public string? ChecksumSha256 { get; private set; }
    public string Status { get; private set; } = default!;
    public string? ErrorMessage { get; private set; }

    public Icd10ImportHistory(string? version, string? checksumSha256)
    {
        Id = Guid.NewGuid(); Version = version; ChecksumSha256 = checksumSha256; Status = "Running"; StartedOnUtc = DateTime.UtcNow;
    }
    public void Complete(int count) { ImportedCodeCount = count; Status = "Succeeded"; CompletedOnUtc = DateTime.UtcNow; }
    public void Fail(string error) { Status = "Failed"; ErrorMessage = error; CompletedOnUtc = DateTime.UtcNow; }
}

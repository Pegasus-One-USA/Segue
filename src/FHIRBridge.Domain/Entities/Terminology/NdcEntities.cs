namespace FHIRBridge.Domain.Entities.Terminology;

/// <summary>One NDC (National Drug Code) product entry, keyed by its 10/11-digit ProductNdc, sourced from the
/// FDA's openFDA National Drug Code Directory export.</summary>
public sealed class NdcProduct
{
    private NdcProduct() { }

    public NdcProduct(string productNdc, string genericName, string? brandName, string? dosageForm, bool isActive, string version)
    {
        ProductNdc = productNdc.Trim();
        GenericName = genericName.Trim();
        BrandName = brandName?.Trim();
        DosageForm = dosageForm?.Trim();
        IsActive = isActive;
        Version = version.Trim();
        CreatedOnUtc = DateTime.UtcNow;
        ModifiedOnUtc = DateTime.UtcNow;
    }

    public string ProductNdc { get; private set; } = default!;
    public string GenericName { get; private set; } = default!;
    public string? BrandName { get; private set; }
    public string? DosageForm { get; private set; }
    public bool IsActive { get; private set; }
    public string Version { get; private set; } = default!;
    public DateTime CreatedOnUtc { get; private set; }
    public DateTime ModifiedOnUtc { get; private set; }

    public void Update(string genericName, string? brandName, string? dosageForm, string version, bool isActive)
    {
        GenericName = genericName.Trim();
        BrandName = brandName?.Trim();
        DosageForm = dosageForm?.Trim();
        Version = version.Trim();
        IsActive = isActive;
        ModifiedOnUtc = DateTime.UtcNow;
    }
}

public sealed class NdcVersion
{
    private NdcVersion() { }
    public Guid Id { get; private set; } = Guid.NewGuid();
    public string Version { get; private set; } = default!;
    public DateTime? ReleaseDateUtc { get; private set; }
    public string? ChecksumSha256 { get; private set; }
    public bool IsActive { get; private set; }
    public DateTime ImportedOnUtc { get; private set; }

    public NdcVersion(string version, DateTime? releaseDateUtc, string? checksumSha256, bool isActive)
    {
        Id = Guid.NewGuid(); Version = version; ReleaseDateUtc = releaseDateUtc; ChecksumSha256 = checksumSha256;
        IsActive = isActive; ImportedOnUtc = DateTime.UtcNow;
    }
    public void SetActive(bool isActive) => IsActive = isActive;
}

public sealed class NdcImportHistory
{
    private NdcImportHistory() { }
    public Guid Id { get; private set; } = Guid.NewGuid();
    public string? Version { get; private set; }
    public DateTime StartedOnUtc { get; private set; }
    public DateTime? CompletedOnUtc { get; private set; }
    public int ImportedConceptCount { get; private set; }
    public string? ChecksumSha256 { get; private set; }
    public string Status { get; private set; } = default!;
    public string? ErrorMessage { get; private set; }

    public NdcImportHistory(string? version, string? checksumSha256)
    {
        Id = Guid.NewGuid(); Version = version; ChecksumSha256 = checksumSha256; Status = "Running"; StartedOnUtc = DateTime.UtcNow;
    }
    public void SetVersion(string version) => Version = version;
    public void Complete(int count) { ImportedConceptCount = count; Status = "Succeeded"; CompletedOnUtc = DateTime.UtcNow; }
    public void Fail(string error) { Status = "Failed"; ErrorMessage = error; CompletedOnUtc = DateTime.UtcNow; }
}

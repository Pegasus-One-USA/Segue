using FHIRBridge.SharedKernel.Abstractions;

namespace FHIRBridge.Domain.Entities.Governance;

/// <summary>One archive-before-purge run for a given data class — the artifact's location and what it covers.</summary>
public sealed class ArchiveManifestEntry : Entity<Guid>
{
    private ArchiveManifestEntry()
    {
    }

    public ArchiveManifestEntry(
        Guid id,
        string dataClass,
        DateTime archivedThroughUtc,
        string fileLocation,
        int recordCount,
        DateTime createdOnUtc)
    {
        Id = id;
        DataClass = dataClass;
        ArchivedThroughUtc = archivedThroughUtc;
        FileLocation = fileLocation;
        RecordCount = recordCount;
        CreatedOnUtc = createdOnUtc;
    }

    public string DataClass { get; private set; } = default!;
    public DateTime ArchivedThroughUtc { get; private set; }
    public string FileLocation { get; private set; } = default!;
    public int RecordCount { get; private set; }
    public DateTime CreatedOnUtc { get; private set; }
}

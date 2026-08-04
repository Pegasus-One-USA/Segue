namespace FHIRBridge.Domain.Entities.Terminology;

/// <summary>One released SNOMED CT concept (RF2 Concept file), keyed by its SCTID. Release data is maintained by the terminology import pipeline.</summary>
public sealed class SnomedConcept
{
    private SnomedConcept() { }

    public SnomedConcept(string id, DateOnly effectiveTime, bool active, string moduleId, string definitionStatusId, string? fsn, string? preferredTerm, string version)
    {
        Id = id.Trim();
        EffectiveTime = effectiveTime;
        Active = active;
        ModuleId = moduleId.Trim();
        DefinitionStatusId = definitionStatusId.Trim();
        Fsn = fsn?.Trim();
        PreferredTerm = preferredTerm?.Trim();
        Version = version.Trim();
        CreatedOnUtc = DateTime.UtcNow;
        ModifiedOnUtc = DateTime.UtcNow;
    }

    /// <summary>The SNOMED CT concept identifier (SCTID).</summary>
    public string Id { get; private set; } = default!;
    public DateOnly EffectiveTime { get; private set; }
    public bool Active { get; private set; }
    public string ModuleId { get; private set; } = default!;
    public string DefinitionStatusId { get; private set; } = default!;
    public string? Fsn { get; private set; }
    public string? PreferredTerm { get; private set; }
    public string Version { get; private set; } = default!;
    public DateTime CreatedOnUtc { get; private set; }
    public DateTime ModifiedOnUtc { get; private set; }

    public void Update(DateOnly effectiveTime, bool active, string moduleId, string definitionStatusId, string? fsn, string? preferredTerm, string version)
    {
        EffectiveTime = effectiveTime; Active = active; ModuleId = moduleId.Trim(); DefinitionStatusId = definitionStatusId.Trim();
        Fsn = fsn?.Trim(); PreferredTerm = preferredTerm?.Trim(); Version = version.Trim(); ModifiedOnUtc = DateTime.UtcNow;
    }

    public void Deactivate() { Active = false; ModifiedOnUtc = DateTime.UtcNow; }
}

/// <summary>One RF2 Description row (FSN/synonym/preferred-term text for a concept), keyed by its RF2 description id.</summary>
public sealed class SnomedDescription
{
    private SnomedDescription() { }

    public SnomedDescription(string id, string conceptId, string term, string typeId, string languageCode, string caseSignificanceId, bool active, string version)
    {
        Id = id.Trim();
        ConceptId = conceptId.Trim();
        Term = term.Trim();
        TypeId = typeId.Trim();
        LanguageCode = languageCode.Trim();
        CaseSignificanceId = caseSignificanceId.Trim();
        Active = active;
        Version = version.Trim();
    }

    public string Id { get; private set; } = default!;
    public string ConceptId { get; private set; } = default!;
    public string Term { get; private set; } = default!;
    public string TypeId { get; private set; } = default!;
    public string LanguageCode { get; private set; } = default!;
    public string CaseSignificanceId { get; private set; } = default!;
    public bool Active { get; private set; }
    public string Version { get; private set; } = default!;
}

/// <summary>One RF2 Relationship row (backs is-a hierarchy walks for $expand), keyed by its RF2 relationship id.</summary>
public sealed class SnomedRelationship
{
    private SnomedRelationship() { }

    public SnomedRelationship(string id, string sourceId, string destinationId, string typeId, int relationshipGroup, string characteristicTypeId, bool active, string version)
    {
        Id = id.Trim();
        SourceId = sourceId.Trim();
        DestinationId = destinationId.Trim();
        TypeId = typeId.Trim();
        RelationshipGroup = relationshipGroup;
        CharacteristicTypeId = characteristicTypeId.Trim();
        Active = active;
        Version = version.Trim();
    }

    public string Id { get; private set; } = default!;
    public string SourceId { get; private set; } = default!;
    public string DestinationId { get; private set; } = default!;
    public string TypeId { get; private set; } = default!;
    public int RelationshipGroup { get; private set; }
    public string CharacteristicTypeId { get; private set; } = default!;
    public bool Active { get; private set; }
    public string Version { get; private set; } = default!;
}

public sealed class SnomedVersion
{
    private SnomedVersion() { }
    public Guid Id { get; private set; } = Guid.NewGuid();
    public string Version { get; private set; } = default!;
    public DateTime? ReleaseDateUtc { get; private set; }
    public string? ChecksumSha256 { get; private set; }
    public bool IsActive { get; private set; }
    public DateTime ImportedOnUtc { get; private set; }

    public SnomedVersion(string version, DateTime? releaseDateUtc, string? checksumSha256, bool isActive)
    {
        Id = Guid.NewGuid(); Version = version; ReleaseDateUtc = releaseDateUtc; ChecksumSha256 = checksumSha256;
        IsActive = isActive; ImportedOnUtc = DateTime.UtcNow;
    }
    public void SetActive(bool isActive) => IsActive = isActive;
}

public sealed class SnomedImportHistory
{
    private SnomedImportHistory() { }
    public Guid Id { get; private set; } = Guid.NewGuid();
    public string? Version { get; private set; }
    public DateTime StartedOnUtc { get; private set; }
    public DateTime? CompletedOnUtc { get; private set; }
    public int ImportedConceptCount { get; private set; }
    public string? ChecksumSha256 { get; private set; }
    public string Status { get; private set; } = default!;
    public string? ErrorMessage { get; private set; }

    public SnomedImportHistory(string? version, string? checksumSha256)
    {
        Id = Guid.NewGuid(); Version = version; ChecksumSha256 = checksumSha256; Status = "Running"; StartedOnUtc = DateTime.UtcNow;
    }
    public void Complete(int count) { ImportedConceptCount = count; Status = "Succeeded"; CompletedOnUtc = DateTime.UtcNow; }
    public void Fail(string error) { Status = "Failed"; ErrorMessage = error; CompletedOnUtc = DateTime.UtcNow; }
}

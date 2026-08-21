namespace FHIRBridge.Domain.Entities.Terminology;

/// <summary>One released LOINC term. Release data is maintained by the terminology import pipeline.</summary>
public sealed class LoincConcept
{
    private LoincConcept() { }

    public LoincConcept(string code, string? display, string? longCommonName, string? version, bool isActive)
    {
        Id = Guid.NewGuid();
        Code = code.Trim();
        Display = display?.Trim();
        LongCommonName = longCommonName?.Trim();
        Version = version?.Trim();
        IsActive = isActive;
        CreatedOnUtc = DateTime.UtcNow;
        ModifiedOnUtc = DateTime.UtcNow;
    }

    public Guid Id { get; private set; }
    public string Code { get; private set; } = default!;
    public string? Display { get; private set; }
    public string? LongCommonName { get; private set; }
    public string? Class { get; private set; }
    public string? Component { get; private set; }
    public string? Property { get; private set; }
    public string? TimeAspect { get; private set; }
    public string? System { get; private set; }
    public string? Scale { get; private set; }
    public string? Method { get; private set; }
    public string? Status { get; private set; }
    public string? Version { get; private set; }
    public bool IsActive { get; private set; }
    public DateTime CreatedOnUtc { get; private set; }
    public DateTime ModifiedOnUtc { get; private set; }

    public void Update(string? display, string? longCommonName, string? @class, string? component, string? property,
        string? timeAspect, string? system, string? scale, string? method, string? status, string? version, bool isActive)
    {
        Display = display?.Trim(); LongCommonName = longCommonName?.Trim(); Class = @class?.Trim();
        Component = component?.Trim(); Property = property?.Trim(); TimeAspect = timeAspect?.Trim();
        System = system?.Trim(); Scale = scale?.Trim(); Method = method?.Trim(); Status = status?.Trim();
        Version = version?.Trim(); IsActive = isActive; ModifiedOnUtc = DateTime.UtcNow;
    }

    public void Deactivate() { IsActive = false; ModifiedOnUtc = DateTime.UtcNow; }
}

public sealed class LoincPart
{
    private LoincPart() { }
    public Guid Id { get; private set; } = Guid.NewGuid();
    public string PartNumber { get; private set; } = default!;
    public string? PartTypeName { get; private set; }
    public string? PartName { get; private set; }
    public string? PartDisplayName { get; private set; }
    public string? Status { get; private set; }
    public string Version { get; private set; } = default!;
}

public sealed class LoincGroup
{
    private LoincGroup() { }

    public LoincGroup(string groupId, string? groupName, string? parentGroupId, string version)
    {
        Id = Guid.NewGuid();
        GroupId = groupId.Trim();
        GroupName = groupName?.Trim();
        ParentGroupId = string.IsNullOrWhiteSpace(parentGroupId) ? null : parentGroupId.Trim();
        Version = version.Trim();
    }

    public Guid Id { get; private set; } = Guid.NewGuid();
    public string GroupId { get; private set; } = default!;
    public string? GroupName { get; private set; }
    public string? ParentGroupId { get; private set; }
    public string Version { get; private set; } = default!;
}

public sealed class LoincAnswerList
{
    private LoincAnswerList() { }

    public LoincAnswerList(string answerListId, string? answerListName, string? loincCode, string? answerCode, string? answerDisplay, string version)
    {
        Id = Guid.NewGuid();
        AnswerListId = answerListId.Trim();
        AnswerListName = answerListName?.Trim();
        LoincCode = string.IsNullOrWhiteSpace(loincCode) ? null : loincCode.Trim();
        AnswerCode = answerCode?.Trim();
        AnswerDisplay = answerDisplay?.Trim();
        Version = version.Trim();
    }

    public Guid Id { get; private set; } = Guid.NewGuid();
    public string AnswerListId { get; private set; } = default!;
    public string? AnswerListName { get; private set; }
    public string? LoincCode { get; private set; }
    public string? AnswerCode { get; private set; }
    public string? AnswerDisplay { get; private set; }
    public string Version { get; private set; } = default!;
}

public sealed class LoincConceptMap
{
    private LoincConceptMap() { }

    public LoincConceptMap(string sourceSystem, string sourceCode, string targetSystem, string targetCode, string? equivalence, string? display, string version)
    {
        Id = Guid.NewGuid();
        SourceSystem = sourceSystem.Trim();
        SourceCode = sourceCode.Trim();
        TargetSystem = targetSystem.Trim();
        TargetCode = targetCode.Trim();
        Equivalence = string.IsNullOrWhiteSpace(equivalence) ? null : equivalence.Trim();
        Display = display?.Trim();
        Version = version.Trim();
    }

    public Guid Id { get; private set; } = Guid.NewGuid();
    public string SourceSystem { get; private set; } = default!;
    public string SourceCode { get; private set; } = default!;
    public string TargetSystem { get; private set; } = default!;
    public string TargetCode { get; private set; } = default!;
    public string? Equivalence { get; private set; }
    public string? Display { get; private set; }
    public string Version { get; private set; } = default!;
}

public sealed class LoincVersion
{
    private LoincVersion() { }
    public Guid Id { get; private set; } = Guid.NewGuid();
    public string Version { get; private set; } = default!;
    public DateTime? ReleaseDateUtc { get; private set; }
    public string? ChecksumSha256 { get; private set; }
    public bool IsActive { get; private set; }
    public DateTime ImportedOnUtc { get; private set; }

    public LoincVersion(string version, DateTime? releaseDateUtc, string? checksumSha256, bool isActive)
    {
        Id = Guid.NewGuid(); Version = version; ReleaseDateUtc = releaseDateUtc; ChecksumSha256 = checksumSha256;
        IsActive = isActive; ImportedOnUtc = DateTime.UtcNow;
    }
    public void SetActive(bool isActive) => IsActive = isActive;
}

public sealed class LoincImportHistory
{
    private LoincImportHistory() { }
    public Guid Id { get; private set; } = Guid.NewGuid();
    public string? Version { get; private set; }
    public DateTime StartedOnUtc { get; private set; }
    public DateTime? CompletedOnUtc { get; private set; }
    public int ImportedConceptCount { get; private set; }
    public string? ChecksumSha256 { get; private set; }
    public string Status { get; private set; } = default!;
    public string? ErrorMessage { get; private set; }

    public LoincImportHistory(string? version, string? checksumSha256)
    {
        Id = Guid.NewGuid(); Version = version; ChecksumSha256 = checksumSha256; Status = "Running"; StartedOnUtc = DateTime.UtcNow;
    }
    public void Complete(int count) { ImportedConceptCount = count; Status = "Succeeded"; CompletedOnUtc = DateTime.UtcNow; }
    public void Fail(string error) { Status = "Failed"; ErrorMessage = error; CompletedOnUtc = DateTime.UtcNow; }
}

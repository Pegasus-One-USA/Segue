namespace FHIRBridge.Domain.Entities.Terminology;

/// <summary>
/// Local MSSQL cache of a HAPI terminology CodeSystem, mirroring HAPI's own internal Postgres schema
/// (table/column names) so a "Run Now" sync writes here instead of PUTting to the remote HAPI server,
/// and lookups become an indexed local SQL query instead of an HTTP round-trip. Corresponds to HAPI's
/// <c>trm_codesystem</c> table.
/// </summary>
public sealed class TrmCodeSystem
{
    private TrmCodeSystem() { }

    public TrmCodeSystem(string codeSystemUri, string? csName)
    {
        CodeSystemUri = codeSystemUri.Trim();
        CsName = csName?.Trim();
    }

    public long Pid { get; private set; }
    public string CodeSystemUri { get; private set; } = default!;
    public string? CsName { get; private set; }
    public long? CurrentVersionPid { get; private set; }

    public void SetCurrentVersion(long versionPid) => CurrentVersionPid = versionPid;
    public void Rename(string? csName) => CsName = csName?.Trim();
}

/// <summary>One synced release/version of a <see cref="TrmCodeSystem"/>. Corresponds to HAPI's <c>trm_codesystem_ver</c> table.</summary>
public sealed class TrmCodeSystemVer
{
    private TrmCodeSystemVer() { }

    public TrmCodeSystemVer(long codeSystemPid, string csVersionId, string? csDisplay)
    {
        CodeSystemPid = codeSystemPid;
        CsVersionId = csVersionId.Trim();
        CsDisplay = csDisplay?.Trim();
    }

    public long Pid { get; private set; }
    public long CodeSystemPid { get; private set; }
    public string CsVersionId { get; private set; } = default!;
    public string? CsDisplay { get; private set; }
}

/// <summary>
/// One code/display concept belonging to a <see cref="TrmCodeSystemVer"/>. Corresponds to HAPI's
/// <c>trm_concept</c> table — including HAPI's own naming quirk where <c>codesystem_pid</c> actually
/// points at the *version* row (<see cref="TrmCodeSystemVer"/>), not the code system itself.
/// </summary>
public sealed class TrmConcept
{
    private TrmConcept() { }

    public TrmConcept(long codeSystemPid, string codeVal, string? display)
    {
        CodeSystemPid = codeSystemPid;
        CodeVal = codeVal.Trim();
        Display = display?.Trim();
    }

    public long Pid { get; private set; }
    public long CodeSystemPid { get; private set; }
    public string CodeVal { get; private set; } = default!;
    public string? Display { get; private set; }

    public void Update(string codeVal, string? display)
    {
        CodeVal = codeVal.Trim();
        Display = display?.Trim();
    }
}

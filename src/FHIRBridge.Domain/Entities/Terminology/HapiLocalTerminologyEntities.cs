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
        : this(codeSystemPid, codeVal, display, null, null, null, true)
    {
    }

    public TrmConcept(
        long codeSystemPid,
        string codeVal,
        string? display,
        string? shortDescription,
        string? longDescription,
        string? longCommonName,
        bool isActive)
    {
        CodeSystemPid = codeSystemPid;
        CodeVal = codeVal.Trim();
        Display = display?.Trim();
        ShortDescription = Normalize(shortDescription);
        LongDescription = Normalize(longDescription);
        LongCommonName = Normalize(longCommonName);
        IsActive = isActive;
    }

    public long Pid { get; private set; }
    public long CodeSystemPid { get; private set; }
    public string CodeVal { get; private set; } = default!;
    public string? Display { get; private set; }

    /// <summary>The source's own abbreviated description, where it publishes one distinct from the long
    /// form (e.g. ICD-10-CM order-file positions 17-76, HCPCS field 8, LOINC SHORTNAME). Null for the
    /// sources that publish only a single description string.</summary>
    public string? ShortDescription { get; private set; }

    /// <summary>The source's own full-length description (e.g. ICD-10-CM order-file position 77+,
    /// HCPCS field 7, DCM skos:definition, MeSH ScopeNote). Null where the source has no long form.</summary>
    public string? LongDescription { get; private set; }

    /// <summary>LOINC's LONG_COMMON_NAME and its per-source equivalents (SNOMED's Fully Specified Name,
    /// RxNorm's prescribable name). Null where the source publishes no such field.</summary>
    public string? LongCommonName { get; private set; }

    /// <summary>Whether this concept is currently in force. Computed at import from whatever status or
    /// expiry signal the source publishes (LOINC STATUS, SNOMED active, RxNorm SUPPRESS, CVX Vaccine
    /// Status, HCPCS termination date, NDC exclude flag, DCM owl:deprecated); defaults to true for the
    /// sources that publish no such signal at all.</summary>
    public bool IsActive { get; private set; } = true;

    public void Update(string codeVal, string? display)
    {
        CodeVal = codeVal.Trim();
        Display = display?.Trim();
    }

    public void UpdateDescriptions(string? shortDescription, string? longDescription, string? longCommonName, bool isActive)
    {
        ShortDescription = Normalize(shortDescription);
        LongDescription = Normalize(longDescription);
        LongCommonName = Normalize(longCommonName);
        IsActive = isActive;
    }

    /// <summary>Collapses whitespace-only source fields to null so "no value" is a single representation —
    /// several sources pad fixed-width columns with spaces, which would otherwise read as a present-but-blank
    /// description and defeat the resolution fallback in CodeableConceptBuilderNode.</summary>
    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

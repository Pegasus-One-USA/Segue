namespace FHIRBridge.Tools.TerminologyServerPoc;

/// <summary>
/// A small, accurate subset of real ICD-10-CM codes used to prove the load/query round-trip
/// against an embedded terminology server. This is NOT the full ICD-10-CM code set (~70,000+
/// codes) — production loading of the complete order file is a separate step (see README.md).
/// </summary>
internal static class Icd10SampleCodeSystem
{
    public const string ResourceId = "icd10cm-demo-subset";
    public const string SystemUrl = "http://hl7.org/fhir/sid/icd-10-cm";

    public static readonly IReadOnlyList<(string Code, string Display)> Concepts = new List<(string, string)>
    {
        ("E11.9", "Type 2 diabetes mellitus without complications"),
        ("I10", "Essential (primary) hypertension"),
        ("J45.909", "Unspecified asthma, uncomplicated"),
        ("E78.5", "Hyperlipidemia, unspecified"),
        ("N39.0", "Urinary tract infection, site not specified"),
        ("J06.9", "Acute upper respiratory infection, unspecified"),
        ("K21.9", "Gastro-esophageal reflux disease without esophagitis"),
        ("F41.9", "Anxiety disorder, unspecified"),
        ("Z00.00", "Encounter for general adult medical examination without abnormal findings"),
        ("E66.9", "Obesity, unspecified"),
    };

    /// <summary>
    /// Builds a FHIR CodeSystem resource (as a plain object graph, serialized by the caller)
    /// containing this sample subset. content="fragment" because this is intentionally a
    /// partial subset, not the complete ICD-10-CM code system.
    /// </summary>
    public static object BuildCodeSystemResource()
    {
        return new
        {
            resourceType = "CodeSystem",
            id = ResourceId,
            url = SystemUrl,
            version = "2025",
            name = "ICD10CMDemoSubset",
            title = "ICD-10-CM (demo subset, 10 common codes)",
            status = "active",
            content = "fragment",
            concept = Concepts.Select(c => new { code = c.Code, display = c.Display }).ToArray(),
        };
    }

    /// <summary>Full-scale resource id — distinct from the demo subset above, and superseding
    /// it once the real download/parse/load path is proven.</summary>
    public const string FullResourceId = "icd10cm-full-2025";

    public static object BuildFullCodeSystemResource(IReadOnlyList<Icd10FullDataSource.Concept> concepts)
    {
        return new
        {
            resourceType = "CodeSystem",
            id = FullResourceId,
            url = SystemUrl,
            version = Icd10FullDataSource.ReleaseYear,
            name = "ICD10CM",
            title = $"ICD-10-CM {Icd10FullDataSource.ReleaseYear} (full, auto-downloaded from CDC)",
            status = "active",
            content = "complete",
            count = concepts.Count,
            concept = concepts.Select(c => new { code = c.Code, display = c.Display }).ToArray(),
        };
    }
}

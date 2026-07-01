namespace FHIRBridge.Application.Services;

/// <summary>
/// Configures Expert Determination (k-anonymity) de-identification, bound from the "ExpertDetermination" section.
/// Disabled by default because it deliberately suppresses records — it is an opt-in research/export control. When
/// enabled, the configured quasi-identifiers are generalized and any record left in a quasi-identifier equivalence
/// class smaller than <see cref="KThreshold"/> is suppressed (dropped).
/// </summary>
public sealed class ExpertDeterminationOptions
{
    public const string SectionName = "ExpertDetermination";

    public bool Enabled { get; set; }

    /// <summary>Minimum equivalence-class size (k). Records in smaller classes are suppressed. Default 5.</summary>
    public int KThreshold { get; set; } = 5;

    /// <summary>
    /// Quasi-identifiers to generalize/evaluate. When empty, a Safe-Harbor-aligned default set for Patient applies
    /// (birthDate→year, address.postalCode→3-digit, gender as-is).
    /// </summary>
    public List<QuasiIdentifierOptions> QuasiIdentifiers { get; set; } = [];

    public static ExpertDeterminationOptions Default { get; } = new();
}

public sealed class QuasiIdentifierOptions
{
    /// <summary>Dotted path within the FHIR resource, e.g. "birthDate" or "address.postalCode".</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>How the quasi-identifier is generalized before forming equivalence classes.</summary>
    public QuasiIdentifierStrategy Strategy { get; set; } = QuasiIdentifierStrategy.AsIs;

    /// <summary>Strategy parameter — e.g. the prefix length for <see cref="QuasiIdentifierStrategy.ZipPrefix"/>.</summary>
    public int Parameter { get; set; } = 3;
}

public enum QuasiIdentifierStrategy
{
    /// <summary>Use the value unchanged (e.g. gender).</summary>
    AsIs = 0,

    /// <summary>Truncate a date to its year (e.g. 1985-06-15 → 1985).</summary>
    DateToYear = 1,

    /// <summary>Keep the leading N characters of a postal code (e.g. 94105 → 941).</summary>
    ZipPrefix = 2,

    /// <summary>Remove the value entirely (always generalizes to the empty class).</summary>
    Redact = 3,
}

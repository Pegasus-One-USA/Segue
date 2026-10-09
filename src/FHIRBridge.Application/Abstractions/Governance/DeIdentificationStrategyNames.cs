namespace FHIRBridge.Application.Abstractions.Governance;

/// <summary>
/// The names a <see cref="DeIdentificationFieldHop.Strategy"/> carries — SafeHarbor writes its
/// DeIdentificationStrategy enum's name there, and that enum lives in FHIRBridge.Infrastructure, out of reach of the
/// Runtime layers that read hops. One definition here instead of string literals scattered across both; a unit test
/// pins these to the enum so they cannot drift.
/// </summary>
public static class DeIdentificationStrategyNames
{
    public const string Remove = "Remove";
    public const string Redact = "Redact";
    public const string Hash = "Hash";
    public const string GeneralizeDateToYear = "GeneralizeDateToYear";
    public const string GeneralizeZip3 = "GeneralizeZip3";
    public const string Mask = "Mask";

    /// <summary>Suffix on a hop that rewrote a reference to a redacted resource ("Hash:Reference").</summary>
    public const string ReferenceSuffix = ":Reference";

    /// <summary>
    /// Strategies that mean the same thing on a transformed string as on the raw value, and so the only ones a
    /// mapped column may apply after its own Transformations. Hash is excluded (a pseudonym of the transformed
    /// value stops matching the hashed references other columns and resources carry), as are GeneralizeDateToYear
    /// and GeneralizeZip3 (they read character positions of the RAW format, which a Transformation may change).
    /// </summary>
    public static readonly IReadOnlySet<string> DeferrableAfterTransformations =
        new HashSet<string>(StringComparer.Ordinal) { Mask, Redact, Remove };
}

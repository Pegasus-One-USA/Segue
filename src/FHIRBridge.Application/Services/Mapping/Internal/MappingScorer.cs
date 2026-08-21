using FHIRBridge.Domain.Enums;

namespace FHIRBridge.Application.Services.Mapping.Internal;

/// <summary>Full weighted score breakdown for one (source field, destination field) pair.</summary>
public sealed record FieldScore(
    double NameScore,
    double SynonymScore,
    double TypeScore,
    double ValueScore,
    double StructureScore,
    double Confidence,
    string Reason);

/// <summary>
/// Combines the four sub-engines (name similarity, synonym dictionary, type compatibility, value-pattern
/// detection) plus structural similarity into one weighted confidence score, per the fixed weights
/// name 40% / synonym 25% / type 15% / value 10% / structure 10%.
/// </summary>
public static class MappingScorer
{
    private const double NameWeight = 0.40;
    private const double SynonymWeight = 0.25;
    private const double TypeWeight = 0.15;
    private const double ValueWeight = 0.10;
    private const double StructureWeight = 0.10;

    public static FieldScore Score(ExtractedSourceField source, string destinationNormalizedName, MappingValueType destinationType)
    {
        var nameScore = SimilarityEngine.NameScore(source.NormalizedName, destinationNormalizedName);
        var synonymScore = SynonymDictionary.Score(source.NormalizedName, destinationNormalizedName);
        var typeScore = TypeCompatibility(source.DataType, destinationType);
        var valueScore = ValuePatternAnalyzer.Score(source.SampleValue, destinationNormalizedName);
        var structureScore = SimilarityEngine.StructuralScore(
            FieldNormalizer.NormalizeStructuralPath(source.StructuralPath), destinationNormalizedName);

        var confidence =
            nameScore * NameWeight +
            synonymScore * SynonymWeight +
            typeScore * TypeWeight +
            valueScore * ValueWeight +
            structureScore * StructureWeight;

        var reason = BuildReason(synonymScore, valueScore, structureScore, nameScore, source);

        return new FieldScore(nameScore, synonymScore, typeScore, valueScore, structureScore, confidence, reason);
    }

    /// <summary>1.0 for an exact type match; partial credit for plausible conversions (e.g. a string sample
    /// that a downstream date-parse transform could still turn into a date); 0.0 for genuinely incompatible pairs.</summary>
    public static double TypeCompatibility(MappingValueType source, MappingValueType destination)
    {
        if (source == destination)
        {
            return 1.0;
        }

        return (source, destination) switch
        {
            (MappingValueType.String, MappingValueType.Date) => 0.6,
            (MappingValueType.String, MappingValueType.DateTime) => 0.6,
            (MappingValueType.Date, MappingValueType.DateTime) => 0.8,
            (MappingValueType.DateTime, MappingValueType.Date) => 0.8,
            (MappingValueType.Integer, MappingValueType.Decimal) => 0.8,
            (MappingValueType.Decimal, MappingValueType.Integer) => 0.5,
            (MappingValueType.String, MappingValueType.Integer) => 0.3,
            (MappingValueType.String, MappingValueType.Decimal) => 0.3,
            (MappingValueType.String, MappingValueType.Boolean) => 0.3,
            (MappingValueType.Json, _) or (_, MappingValueType.Json) => 0.2,
            _ => 0.0
        };
    }

    private static string BuildReason(
        double synonymScore, double valueScore, double structureScore, double nameScore, ExtractedSourceField source)
    {
        var reasons = new List<string>();

        if (synonymScore >= 1.0)
        {
            reasons.Add($"'{source.NormalizedName}' synonym");
        }

        var pattern = ValuePatternAnalyzer.DetectPattern(source.SampleValue ?? string.Empty);
        if (valueScore >= 0.8 && pattern is not null)
        {
            reasons.Add($"{pattern} value pattern");
        }

        if (structureScore >= 0.8)
        {
            reasons.Add("structural path match");
        }

        if (reasons.Count == 0 && nameScore >= 0.8)
        {
            reasons.Add("strong name similarity");
        }

        return reasons.Count > 0 ? string.Join(" + ", reasons) : "weak match — review recommended";
    }
}

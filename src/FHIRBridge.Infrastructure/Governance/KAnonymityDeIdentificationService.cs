using System.Text.Json.Nodes;
using FHIRBridge.Application.Abstractions.Governance;
using FHIRBridge.Application.Services;

namespace FHIRBridge.Infrastructure.Governance;

/// <summary>
/// Expert Determination de-identification via k-anonymity. Generalizes the configured quasi-identifiers in place
/// (e.g. birthDate→year, ZIP→3-digit), groups the cohort into quasi-identifier equivalence classes, and suppresses
/// (drops) every record whose class is smaller than <c>k</c>. The surviving, generalized resources satisfy
/// k-anonymity for the configured quasi-identifier set. Resources that cannot be parsed are passed through unchanged
/// and excluded from the guarantee.
/// </summary>
public sealed class KAnonymityDeIdentificationService : IDataSetDeIdentificationService
{
    private static readonly IReadOnlyList<QuasiIdentifierOptions> DefaultQuasiIdentifiers =
    [
        new() { Path = "birthDate", Strategy = QuasiIdentifierStrategy.DateToYear },
        new() { Path = "address.postalCode", Strategy = QuasiIdentifierStrategy.ZipPrefix, Parameter = 3 },
        new() { Path = "gender", Strategy = QuasiIdentifierStrategy.AsIs },
    ];

    private readonly ExpertDeterminationOptions _options;

    public KAnonymityDeIdentificationService(ExpertDeterminationOptions? options = null)
    {
        _options = options ?? ExpertDeterminationOptions.Default;
    }

    public Task<DataSetDeIdentificationResult> DeIdentifyAsync(
        DataSetDeIdentificationRequest request,
        CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            return Task.FromResult(new DataSetDeIdentificationResult(
                request.ResourcesJson, request.ResourcesJson.Count, 0, 0));
        }

        var k = Math.Max(1, _options.KThreshold);
        var quasiIdentifiers = _options.QuasiIdentifiers.Count > 0
            ? _options.QuasiIdentifiers
            : DefaultQuasiIdentifiers;

        // Generalize each resource and compute its equivalence-class key.
        var entries = new List<(string Json, string? Key)>(request.ResourcesJson.Count);
        foreach (var json in request.ResourcesJson)
        {
            if (JsonNode.Parse(json) is not JsonObject resource)
            {
                entries.Add((json, null)); // unparseable: pass through, excluded from the guarantee
                continue;
            }

            var keyParts = new List<string>(quasiIdentifiers.Count);
            foreach (var qi in quasiIdentifiers)
            {
                keyParts.Add(GeneralizeQuasiIdentifier(resource, qi));
            }

            entries.Add((resource.ToJsonString(), string.Join("|", keyParts)));
        }

        // Equivalence-class sizes over the keyed (parseable) records.
        var classSizes = entries
            .Where(e => e.Key is not null)
            .GroupBy(e => e.Key!)
            .ToDictionary(g => g.Key, g => g.Count());

        var surviving = new List<string>(entries.Count);
        var suppressed = 0;
        foreach (var (json, key) in entries)
        {
            if (key is not null && classSizes.TryGetValue(key, out var size) && size < k)
            {
                suppressed++;
                continue;
            }

            surviving.Add(json);
        }

        return Task.FromResult(new DataSetDeIdentificationResult(surviving, request.ResourcesJson.Count, suppressed, k));
    }

    /// <summary>Generalizes the quasi-identifier in place and returns its generalized token for the class key.</summary>
    private static string GeneralizeQuasiIdentifier(JsonObject resource, QuasiIdentifierOptions qi)
    {
        var segments = qi.Path.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var collected = new List<string>();
        ApplyAndCollect(resource, segments, 0, qi, collected);
        return collected.Count > 0 ? collected[0] : string.Empty;
    }

    private static void ApplyAndCollect(JsonNode? node, string[] segments, int index, QuasiIdentifierOptions qi, List<string> collected)
    {
        switch (node)
        {
            case null:
                return;
            case JsonArray array:
                foreach (var item in array)
                {
                    ApplyAndCollect(item, segments, index, qi, collected);
                }

                return;
            case JsonObject obj:
                var key = segments[index];
                if (index == segments.Length - 1)
                {
                    if (obj[key] is JsonValue value)
                    {
                        var generalized = Generalize(AsString(value), qi);
                        obj[key] = generalized;
                        collected.Add(generalized);
                    }

                    return;
                }

                ApplyAndCollect(obj[key], segments, index + 1, qi, collected);
                return;
        }
    }

    private static string Generalize(string value, QuasiIdentifierOptions qi) => qi.Strategy switch
    {
        QuasiIdentifierStrategy.DateToYear => value.Length >= 4 ? value[..4] : value,
        QuasiIdentifierStrategy.ZipPrefix => value.Length >= qi.Parameter ? value[..Math.Max(0, qi.Parameter)] : value,
        QuasiIdentifierStrategy.Redact => string.Empty,
        _ => value,
    };

    private static string AsString(JsonValue value)
    {
        try
        {
            return value.GetValue<string>();
        }
        catch (InvalidOperationException)
        {
            return value.ToJsonString().Trim('"');
        }
    }
}

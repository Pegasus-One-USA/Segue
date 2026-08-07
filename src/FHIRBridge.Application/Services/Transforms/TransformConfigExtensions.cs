namespace FHIRBridge.Application.Services.Transforms;

internal static class TransformConfigExtensions
{
    public static string Get(this IReadOnlyDictionary<string, string> config, string key, string defaultValue = "") =>
        config.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : defaultValue;

    public static string? GetOrNull(this IReadOnlyDictionary<string, string> config, string key) =>
        config.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;

    public static bool GetBool(this IReadOnlyDictionary<string, string> config, string key, bool defaultValue = false) =>
        config.TryGetValue(key, out var value) && bool.TryParse(value, out var parsed) ? parsed : defaultValue;

    public static int GetInt(this IReadOnlyDictionary<string, string> config, string key, int defaultValue = 0) =>
        config.TryGetValue(key, out var value) && int.TryParse(value, out var parsed) ? parsed : defaultValue;

    /// <summary>Treats <paramref name="value"/> as a sequence of candidates/items — a bare scalar (including a
    /// string, which is itself <see cref="System.Collections.IEnumerable"/> over its characters and must NOT be
    /// iterated char-by-char here) becomes a single-item sequence; an actual collection is enumerated as-is.</summary>
    public static IEnumerable<object?> AsItems(this object? value) => value switch
    {
        null => [],
        string s => [s],
        System.Collections.IEnumerable enumerable => enumerable.Cast<object?>(),
        _ => [value]
    };
}

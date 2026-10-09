using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace FHIRBridge.Infrastructure.Destinations.EhrWriteBack;

/// <summary>
/// The hashed keys of an <c>EhrWriteLedgerEntry</c>. SHA-256 as uppercase hex, always the same case on both database
/// providers. Inputs keep their case: FHIR ids are case-sensitive, and SQL Server compares case-insensitively.
/// </summary>
internal static class EhrWriteKeys
{
    /// <summary>The EHR environment: its FHIR base URL without a trailing slash, scheme and host lower-cased.</summary>
    public static string TargetKey(string targetBaseUrl) => Hash("target|" + NormalizeBaseUrl(targetBaseUrl));

    /// <summary>The record's origin. <paramref name="sourceBaseUrl"/> is null when the run's source is not a single
    /// known FHIR server; the key then relies on the source id alone. <paramref name="clone"/> keys a clone-mode
    /// write apart from a normal one, so writing a record to its clone never counts as writing it for real.</summary>
    public static string SourceKey(string? sourceBaseUrl, string resourceType, string sourceId, bool clone = false) =>
        Hash($"source|{(clone ? "clone|" : string.Empty)}{NormalizeBaseUrl(sourceBaseUrl)}|{resourceType}|{sourceId}");

    /// <summary>The shaped resource as it would be sent. JsonNode serialisation keeps property order, and every
    /// profile builds its output in a fixed order, so the same input always hashes the same.</summary>
    public static string ContentHash(JsonObject shaped) => Hash("content|" + shaped.ToJsonString());

    public static string NormalizeBaseUrl(string? baseUrl)
    {
        var trimmed = (baseUrl ?? string.Empty).Trim().TrimEnd('/');
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            return trimmed;
        }

        var authority = uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}";
        return $"{uri.Scheme.ToLowerInvariant()}://{authority.ToLowerInvariant()}{uri.AbsolutePath.TrimEnd('/')}";
    }

    public static bool SameEnvironment(string? left, string? right) =>
        !string.IsNullOrWhiteSpace(left)
        && !string.IsNullOrWhiteSpace(right)
        && string.Equals(NormalizeBaseUrl(left), NormalizeBaseUrl(right), StringComparison.Ordinal);

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}

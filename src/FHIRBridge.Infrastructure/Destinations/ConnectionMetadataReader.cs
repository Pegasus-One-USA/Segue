using System.Text.Json;

namespace FHIRBridge.Infrastructure.Destinations;

/// <summary>
/// Reads a single flat, non-secret field out of <see cref="Domain.Entities.DestinationConfiguration.ConnectionMetadataJson"/>
/// (a JSON object of <c>dest_*</c>-prefixed keys built by the wizard). Tolerant of a null/empty/malformed document —
/// destination rows saved before a field existed simply fall back to the caller-supplied default.
/// </summary>
internal static class ConnectionMetadataReader
{
    public static string? GetString(string? connectionMetadataJson, string key)
    {
        if (string.IsNullOrWhiteSpace(connectionMetadataJson))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(connectionMetadataJson);
            return document.RootElement.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static int GetInt(string? connectionMetadataJson, string key, int fallback)
    {
        var raw = GetString(connectionMetadataJson, key);
        return int.TryParse(raw, out var value) ? value : fallback;
    }

    public static bool GetBool(string? connectionMetadataJson, string key, bool fallback)
    {
        var raw = GetString(connectionMetadataJson, key);
        return bool.TryParse(raw, out var value) ? value : fallback;
    }
}

using System.Text.Json;
using FHIRBridge.Runtime.Application.Workflows.Catalog;
using FHIRBridge.Runtime.Domain.Workflows;

namespace FHIRBridge.Runtime.Application.Workflows;

/// <summary>
/// The EHRs a workflow writes into for real, read off its saved EHR Write-Back nodes. A node is live when it is
/// enabled, <c>dest_dryRun</c> is explicitly <c>false</c> (anything else is a dry run, as in the executor) and
/// <c>dest_testAsVendor</c> is empty (a test run sends to a FHIR test server, never the EHR). Its EHR is
/// <c>dest_ehrVendor</c>. Backs the workflow list's "Writes to ..." badge.
/// </summary>
public static class LiveEhrWriteTargets
{
    /// <summary>The vendor codes (e.g. <c>Epic</c>, <c>Healow</c>) this workflow writes into live, each once, in node
    /// order. Empty when every write-back is a test or dry run, or there is none.</summary>
    public static IReadOnlyList<string> Of(IEnumerable<WorkflowNode> nodes)
    {
        var vendors = new List<string>();
        foreach (var node in nodes)
        {
            if (!node.IsEnabled
                || !string.Equals(node.NodeType, WorkflowNodeTypes.EhrWriteBackDestination, StringComparison.OrdinalIgnoreCase)
                || ReadVendorIfLive(node.ConfigurationJson) is not { } vendor
                || vendors.Contains(vendor, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            vendors.Add(vendor);
        }

        return vendors;
    }

    private static string? ReadVendorIfLive(string configurationJson)
    {
        try
        {
            using var document = JsonDocument.Parse(configurationJson);
            if (!WorkflowNodeConfigurationEnvelope.TryGetSettings(document, out var settings)
                || settings.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var isDryRun = !bool.TryParse(ReadString(settings, "dest_dryRun"), out var dryRun) || dryRun;
            if (isDryRun || !string.IsNullOrWhiteSpace(ReadString(settings, "dest_testAsVendor")))
            {
                return null;
            }

            var vendor = ReadString(settings, "dest_ehrVendor");
            return string.IsNullOrWhiteSpace(vendor) ? null : vendor.Trim();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ReadString(JsonElement settings, string key) =>
        settings.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}

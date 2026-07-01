using System.Text.Json;
using FHIRBridge.Runtime.Domain.ValueObjects;

namespace FHIRBridge.Integration.Fhir;

/// <summary>
/// Shared FHIR JSON parsing used by both the runtime connectors (search/pagination) and the configured pipeline
/// (webhook payloads). Produces <see cref="ResourceEnvelope"/> values and is the single source of truth for turning
/// FHIR JSON into resources across stacks.
/// </summary>
public static class FhirResourceParser
{
    /// <summary>Parses a single FHIR resource document.</summary>
    public static ResourceEnvelope ParseResource(string rawJson)
    {
        using var document = JsonDocument.Parse(rawJson);

        return ParseResource(document.RootElement);
    }

    /// <summary>
    /// Parses a FHIR search response. A search <c>Bundle</c> yields its entries (an empty bundle yields no resources);
    /// a no-results <c>OperationOutcome</c> yields no resources, while any other <c>OperationOutcome</c> throws.
    /// </summary>
    public static IReadOnlyList<ResourceEnvelope> ParseSearchBundle(string rawJson)
    {
        using var document = JsonDocument.Parse(rawJson);
        var root = document.RootElement;

        var resourceType = GetString(root, "resourceType");
        if (resourceType == "OperationOutcome")
        {
            if (IsNoResultsOperationOutcome(root))
            {
                return [];
            }

            throw new InvalidOperationException($"FHIR search returned OperationOutcome: {GetOperationOutcomeMessage(root)}");
        }

        if (resourceType != "Bundle")
        {
            return [ParseResource(root)];
        }

        return ParseBundleEntries(root);
    }

    /// <summary>
    /// Parses an inbound FHIR payload (for example a webhook body). A <c>Bundle</c> must contain at least one resource;
    /// otherwise the single resource is returned. Unlike <see cref="ParseSearchBundle"/>, an empty bundle is an error.
    /// </summary>
    public static IReadOnlyList<ResourceEnvelope> ParsePayload(string rawJson)
    {
        using var document = JsonDocument.Parse(rawJson);
        var root = document.RootElement;

        if (string.Equals(GetString(root, "resourceType"), "Bundle", StringComparison.OrdinalIgnoreCase))
        {
            var resources = ParseBundleEntries(root);
            if (resources.Count == 0)
            {
                throw new InvalidOperationException("FHIR Bundle payload did not contain any resources.");
            }

            return resources;
        }

        return [ParseResource(root)];
    }

    /// <summary>Returns the <c>next</c> page link of a search <c>Bundle</c>, or null when there is none.</summary>
    public static string? GetNextLink(string rawJson)
    {
        using var document = JsonDocument.Parse(rawJson);
        var root = document.RootElement;

        if (GetString(root, "resourceType") != "Bundle" ||
            !root.TryGetProperty("link", out var links) ||
            links.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var link in links.EnumerateArray())
        {
            if (GetString(link, "relation") == "next")
            {
                return GetString(link, "url");
            }
        }

        return null;
    }

    private static IReadOnlyList<ResourceEnvelope> ParseBundleEntries(JsonElement bundle)
    {
        if (!bundle.TryGetProperty("entry", out var entries) || entries.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var resources = new List<ResourceEnvelope>();

        foreach (var entry in entries.EnumerateArray())
        {
            // Skip informational search-outcome entries (e.g. Epic/HAPI include an OperationOutcome with
            // search.mode = "outcome" in a searchset Bundle). These are not data resources and must not be mapped.
            if (entry.TryGetProperty("search", out var search) &&
                search.ValueKind == JsonValueKind.Object &&
                string.Equals(GetString(search, "mode"), "outcome", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!entry.TryGetProperty("resource", out var resource) ||
                resource.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            // OperationOutcome resources are diagnostics, never extractable data — skip regardless of search mode.
            if (string.Equals(GetString(resource, "resourceType"), "OperationOutcome", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            resources.Add(ParseResource(resource));
        }

        return resources;
    }

    private static ResourceEnvelope ParseResource(JsonElement resource)
    {
        var resourceType = GetString(resource, "resourceType");
        if (string.IsNullOrWhiteSpace(resourceType))
        {
            throw new InvalidOperationException("FHIR resource payload is missing resourceType.");
        }

        string? versionId = null;
        DateTimeOffset? lastUpdated = null;

        if (resource.TryGetProperty("meta", out var meta) && meta.ValueKind == JsonValueKind.Object)
        {
            versionId = GetString(meta, "versionId");
            if (DateTimeOffset.TryParse(GetString(meta, "lastUpdated"), out var parsedLastUpdated))
            {
                lastUpdated = parsedLastUpdated;
            }
        }

        return new ResourceEnvelope(
            resourceType,
            GetString(resource, "id"),
            JsonSerializer.Serialize(resource),
            versionId,
            lastUpdated);
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }

    private static bool IsNoResultsOperationOutcome(JsonElement root)
    {
        return GetOperationOutcomeIssues(root).Any(issue =>
            string.Equals(GetString(issue, "severity"), "warning", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(GetString(issue, "code"), "processing", StringComparison.OrdinalIgnoreCase) &&
            ContainsNoResultsText(issue));
    }

    private static string GetOperationOutcomeMessage(JsonElement root)
    {
        var messages = GetOperationOutcomeIssues(root)
            .Select(GetIssueMessage)
            .Where(message => !string.IsNullOrWhiteSpace(message))
            .ToArray();

        return messages.Length == 0
            ? "No issue details were provided."
            : string.Join("; ", messages);
    }

    private static IEnumerable<JsonElement> GetOperationOutcomeIssues(JsonElement root)
    {
        if (!root.TryGetProperty("issue", out var issues) || issues.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return issues.EnumerateArray().ToArray();
    }

    private static bool ContainsNoResultsText(JsonElement issue)
    {
        return GetIssueMessage(issue).Contains("no results", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetIssueMessage(JsonElement issue)
    {
        var diagnostics = GetString(issue, "diagnostics");
        if (!string.IsNullOrWhiteSpace(diagnostics))
        {
            return diagnostics;
        }

        if (issue.TryGetProperty("details", out var details))
        {
            var text = GetString(details, "text");
            if (!string.IsNullOrWhiteSpace(text))
            {
                return text;
            }
        }

        return string.Empty;
    }
}

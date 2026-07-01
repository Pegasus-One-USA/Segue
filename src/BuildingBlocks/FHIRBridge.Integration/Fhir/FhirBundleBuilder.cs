using System.Text.Json.Nodes;
using FHIRBridge.Runtime.Domain.ValueObjects;

namespace FHIRBridge.Integration.Fhir;

/// <summary>
/// Builds a FHIR <c>searchset</c> Bundle from already-parsed <see cref="ResourceEnvelope"/> values, the sibling
/// to <see cref="FhirResourceParser"/>. Pure System.Text.Json (no Firely): each envelope's <c>RawJson</c> is embedded
/// verbatim as <c>entry.resource</c>. Per-type fetch failures are appended as <c>OperationOutcome</c> entries so a
/// best-effort aggregation can report partial failure inline rather than failing the whole request.
/// </summary>
public static class FhirBundleBuilder
{
    /// <summary>
    /// Builds a searchset Bundle. <paramref name="resources"/> become <c>match</c> entries (and define
    /// <c>total</c>); <paramref name="failures"/> become <c>outcome</c> <c>OperationOutcome</c> entries.
    /// </summary>
    public static string Build(
        IEnumerable<ResourceEnvelope> resources,
        IEnumerable<ResourceFetchFailure>? failures = null)
    {
        ArgumentNullException.ThrowIfNull(resources);

        var entries = new JsonArray();
        var total = 0;

        foreach (var envelope in resources)
        {
            entries.Add(BuildResourceEntry(envelope));
            total++;
        }

        if (failures is not null)
        {
            foreach (var failure in failures)
            {
                entries.Add(BuildOutcomeEntry(failure));
            }
        }

        var bundle = new JsonObject
        {
            ["resourceType"] = "Bundle",
            ["type"] = "searchset",
            ["total"] = total,
            ["entry"] = entries
        };

        return bundle.ToJsonString();
    }

    private static JsonObject BuildResourceEntry(ResourceEnvelope envelope)
    {
        // Parse RawJson back into a node so it is embedded as a real object, not a JSON string literal.
        var resource = JsonNode.Parse(envelope.RawJson)
            ?? throw new InvalidOperationException($"Resource envelope for '{envelope.ResourceType}' contained null JSON.");

        var entry = new JsonObject
        {
            ["resource"] = resource,
            ["search"] = new JsonObject { ["mode"] = "match" }
        };

        var fullUrl = BuildFullUrl(envelope.ResourceType, envelope.ResourceId);
        if (fullUrl is not null)
        {
            entry["fullUrl"] = fullUrl;
        }

        return entry;
    }

    private static JsonObject BuildOutcomeEntry(ResourceFetchFailure failure)
    {
        var diagnostics = string.IsNullOrWhiteSpace(failure.Message)
            ? $"Failed to retrieve {failure.ResourceType} resources."
            : $"Failed to retrieve {failure.ResourceType} resources: {failure.Message}";

        var outcome = new JsonObject
        {
            ["resourceType"] = "OperationOutcome",
            ["issue"] = new JsonArray
            {
                new JsonObject
                {
                    ["severity"] = "error",
                    ["code"] = "exception",
                    ["diagnostics"] = diagnostics
                }
            }
        };

        return new JsonObject
        {
            ["resource"] = outcome,
            ["search"] = new JsonObject { ["mode"] = "outcome" }
        };
    }

    private static string? BuildFullUrl(string resourceType, string? resourceId)
    {
        if (string.IsNullOrWhiteSpace(resourceType) || string.IsNullOrWhiteSpace(resourceId))
        {
            return null;
        }

        return $"{resourceType}/{resourceId}";
    }
}

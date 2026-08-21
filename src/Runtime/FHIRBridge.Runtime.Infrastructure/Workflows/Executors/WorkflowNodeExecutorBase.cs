using FHIRBridge.Runtime.Application.Workflows;
using FHIRBridge.Runtime.Domain.Workflows;
using System.Text.Json;

namespace FHIRBridge.Runtime.Infrastructure.Workflows.Executors;

public abstract class WorkflowNodeExecutorBase : IWorkflowNodeExecutor
{
    protected WorkflowNodeExecutorBase(string nodeType, WorkflowDataContract outputContract)
    {
        NodeType = nodeType;
        OutputContract = outputContract;
    }

    public string NodeType { get; }

    protected WorkflowDataContract OutputContract { get; }

    public virtual Task<WorkflowNodeOutput> ExecuteAsync(
        WorkflowExecutionContext context,
        WorkflowNode node,
        IReadOnlyCollection<WorkflowNodeOutput> inputs,
        CancellationToken cancellationToken)
    {
        var payload = CreatePayload(context, node, inputs);
        var metadata = new Dictionary<string, object?>
        {
            ["executor"] = GetType().Name,
            ["adapterStatus"] = "Placeholder until existing Segue service is wired behind this node."
        };

        return Task.FromResult(new WorkflowNodeOutput(node.Id, node.NodeType, payload, OutputContract, metadata));
    }

    protected abstract object? CreatePayload(
        WorkflowExecutionContext context,
        WorkflowNode node,
        IReadOnlyCollection<WorkflowNodeOutput> inputs);

    protected static T? ReadConfiguration<T>(WorkflowNode node, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(node.ConfigurationJson))
        {
            return default;
        }

        using var document = JsonDocument.Parse(node.ConfigurationJson);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            return default;
        }

        if (!document.RootElement.TryGetProperty(propertyName, out var property))
        {
            return default;
        }

        // Speculative read: the caller doesn't know in advance whether this property is actually shaped like T (e.g.
        // a wizard-authored node's raw field bag under a matching key name, or a genuinely-typed embedded config from
        // the route→graph projection). A shape mismatch should fall through to the next resolution strategy, not
        // crash the whole run with a raw JsonException.
        try
        {
            // Wizard-authored nodes serialize every config value as a string (BaseNode.fields is Record<string,string>),
            // so a structured value such as "fields" or "mappingProfile" arrives JSON-encoded *inside* a JSON string
            // (e.g. "[{\"targetField\":...}]"). Deserializing that string token straight into a collection/object
            // throws and would silently yield an empty config (an upsert SQL destination then failing with
            // "no mapped field is designated as the upsert key"). Unwrap the one extra layer first — unless T itself
            // is string, where the raw value is what the caller wants.
            if (property.ValueKind == JsonValueKind.String && typeof(T) != typeof(string))
            {
                var inner = property.GetString();
                return string.IsNullOrWhiteSpace(inner) ? default : JsonSerializer.Deserialize<T>(inner, JsonOptions);
            }

            return property.Deserialize<T>(JsonOptions);
        }
        catch (JsonException)
        {
            // The wizard flattens every node property to a string when it persists ConfigurationJson (matching the
            // Key/Value shape of WorkflowNodeConfigurations), so a structured value like "fields" or "mappingProfileIds"
            // arrives here JSON-encoded *inside* a string token rather than as a native array/object. Unwrap and retry
            // once before giving up, or a wizard-authored node silently loses the property (e.g. an upsert-key flag
            // that resolves to an empty Fields collection instead of the field it was actually set on).
            if (property.ValueKind == JsonValueKind.String)
            {
                try
                {
                    return JsonSerializer.Deserialize<T>(property.GetString() ?? string.Empty, JsonOptions);
                }
                catch (JsonException)
                {
                    return default;
                }
            }

            return default;
        }
    }

    protected static T? ReadConfiguration<T>(WorkflowNode node)
    {
        if (string.IsNullOrWhiteSpace(node.ConfigurationJson))
        {
            return default;
        }

        // Same rationale as above: this overload guesses that the *entire* node config already matches T's shape
        // (used by hand-authored/route-projected configs). A wizard-authored node whose raw field bag doesn't match
        // — e.g. "Scopes" as a space-joined string instead of a JSON array — must fall through cleanly rather than
        // bubble a JsonException up through the workflow run.
        try
        {
            return JsonSerializer.Deserialize<T>(node.ConfigurationJson, JsonOptions);
        }
        catch (JsonException)
        {
            return default;
        }
    }

    protected static string? ReadStringConfiguration(WorkflowNode node, string propertyName)
    {
        using var document = JsonDocument.Parse(node.ConfigurationJson);
        return document.RootElement.ValueKind == JsonValueKind.Object
            && document.RootElement.TryGetProperty(propertyName, out var property)
            && property.ValueKind == JsonValueKind.String
                ? property.GetString()
                : null;
    }

    /// <summary>
    /// Reads this exact node's own <c>mappingProfileIds</c> (a JSON object of <c>{resourceType: mappingProfileId}</c>
    /// the build endpoint stamps — see WorkflowEndpoints.cs's Mappings step) falling back to the legacy single
    /// <c>mappingProfileId</c> field for a node saved before per-resource ids existed (attributed to
    /// <c>resourceType</c>, defaulting to "Patient" for the oldest graphs that predate that field too). Resolving
    /// a resource type's mapping this way — by an id THIS node itself saved — can never pick up a different
    /// workflow's profile, unlike a search keyed on (ResourceType, SourceConnectionId, DestinationId), which is
    /// shared by any workflow built on the same source connection + destination + resource type.
    /// </summary>
    protected static Dictionary<string, Guid> ReadProfileIds(WorkflowNode node)
    {
        var mappingProfileIds = ReadConfiguration<Dictionary<string, string>>(node, "mappingProfileIds");
        var result = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        if (mappingProfileIds is { Count: > 0 })
        {
            foreach (var (resourceType, id) in mappingProfileIds)
            {
                if (Guid.TryParse(id, out var parsed))
                {
                    result[resourceType] = parsed;
                }
            }

            if (result.Count > 0)
            {
                return result;
            }
        }

        var single = ReadStringConfiguration(node, "mappingProfileId");
        var legacyResourceType = ReadStringConfiguration(node, "resourceType") ?? "Patient";
        if (Guid.TryParse(single, out var singleId))
        {
            result[legacyResourceType] = singleId;
        }

        return result;
    }

    protected static bool? ReadBoolConfiguration(WorkflowNode node, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(node.ConfigurationJson))
        {
            return null;
        }

        using var document = JsonDocument.Parse(node.ConfigurationJson);
        return document.RootElement.ValueKind == JsonValueKind.Object
            && document.RootElement.TryGetProperty(propertyName, out var property)
            && property.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? property.GetBoolean()
                : null;
    }

    protected static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
}

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
            ["adapterStatus"] = "Placeholder until existing FHIRBridge service is wired behind this node."
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
            return property.Deserialize<T>(JsonOptions);
        }
        catch (JsonException)
        {
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

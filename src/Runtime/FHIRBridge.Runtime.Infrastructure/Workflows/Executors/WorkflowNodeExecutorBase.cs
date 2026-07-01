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

        return property.Deserialize<T>(JsonOptions);
    }

    protected static T? ReadConfiguration<T>(WorkflowNode node)
        => string.IsNullOrWhiteSpace(node.ConfigurationJson)
            ? default
            : JsonSerializer.Deserialize<T>(node.ConfigurationJson, JsonOptions);

    protected static string? ReadStringConfiguration(WorkflowNode node, string propertyName)
    {
        using var document = JsonDocument.Parse(node.ConfigurationJson);
        return document.RootElement.ValueKind == JsonValueKind.Object
            && document.RootElement.TryGetProperty(propertyName, out var property)
            && property.ValueKind == JsonValueKind.String
                ? property.GetString()
                : null;
    }

    protected static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
}

using FHIRBridge.Domain.Enums;

namespace FHIRBridge.Application.Services.Transforms;

/// <summary>Resolves a <see cref="TransformNodeType"/> to its <see cref="ITransformNode"/> implementation —
/// registry-over-switch, per this codebase's architecture convention: adding a 21st node means registering one
/// more <see cref="ITransformNode"/> in DI, never touching this class.</summary>
public interface ITransformNodeRegistry
{
    ITransformNode Get(TransformNodeType nodeType);
}

public sealed class TransformNodeRegistry : ITransformNodeRegistry
{
    private readonly IReadOnlyDictionary<TransformNodeType, ITransformNode> _nodesByType;

    public TransformNodeRegistry(IEnumerable<ITransformNode> nodes)
    {
        _nodesByType = nodes.ToDictionary(n => n.NodeType);
    }

    public ITransformNode Get(TransformNodeType nodeType) =>
        _nodesByType.TryGetValue(nodeType, out var node)
            ? node
            : throw new InvalidOperationException($"No ITransformNode registered for '{nodeType}'.");
}

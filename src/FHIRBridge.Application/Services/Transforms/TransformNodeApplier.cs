using System.Collections;
using FHIRBridge.Domain.Enums;

namespace FHIRBridge.Application.Services.Transforms;

/// <summary>
/// Applies <see cref="TransformArrayMode"/> around a node call — <see cref="TransformArrayMode.Whole"/> (the
/// historical, still-default behavior) runs the node once against the value as-is; <see
/// cref="TransformArrayMode.PerItem"/> runs it once per element when the value is a real collection (a
/// string is never treated as one), reassembling the results into an array. A single per-item failure fails
/// the whole call — partial per-item output isn't a well-formed result for the rule's own error policy to
/// reason about.
/// </summary>
public static class TransformNodeApplier
{
    public static TransformResult ExecuteWithArrayMode(
        ITransformNode node, object? value, IReadOnlyDictionary<string, string> config, string? secret, TransformArrayMode arrayMode)
    {
        if (arrayMode != TransformArrayMode.PerItem || value is null or string || value is not IEnumerable items)
        {
            return node.Execute(value, config, secret);
        }

        var results = new List<object?>();
        foreach (var item in items)
        {
            var result = node.Execute(item, config, secret);
            if (!result.Success)
            {
                return result;
            }

            results.Add(result.Value);
        }

        return TransformResult.Ok(results.ToArray());
    }

    /// <summary>Async twin of <see cref="ExecuteWithArrayMode"/> — routes through <see cref="ITransformNode.ExecuteAsync"/>
    /// so a node backed by a real dependency (e.g. a DB-backed terminology lookup) can await it, while every other
    /// node's default <see cref="ITransformNode.ExecuteAsync"/> body just wraps its synchronous <c>Execute</c>.
    /// <paramref name="onItemExecuted"/>, when supplied, is invoked once per element for
    /// <see cref="TransformArrayMode.PerItem"/> (with the per-item input value and its own <see cref="TransformResult"/>)
    /// — without it, a lineage capturer watching only the outer call would see one array in, one array out, and lose
    /// which source element produced which destination element.</summary>
    public static async Task<TransformResult> ExecuteWithArrayModeAsync(
        ITransformNode node, object? value, IReadOnlyDictionary<string, string> config, string? secret,
        TransformArrayMode arrayMode, CancellationToken cancellationToken = default,
        Action<object?, TransformResult>? onItemExecuted = null)
    {
        if (arrayMode != TransformArrayMode.PerItem || value is null or string || value is not IEnumerable items)
        {
            return await node.ExecuteAsync(value, config, secret, cancellationToken);
        }

        var results = new List<object?>();
        foreach (var item in items)
        {
            var result = await node.ExecuteAsync(item, config, secret, cancellationToken);
            onItemExecuted?.Invoke(item, result);
            if (!result.Success)
            {
                return result;
            }

            results.Add(result.Value);
        }

        return TransformResult.Ok(results.ToArray());
    }
}

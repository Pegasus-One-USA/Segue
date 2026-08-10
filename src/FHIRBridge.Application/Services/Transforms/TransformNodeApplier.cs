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
}

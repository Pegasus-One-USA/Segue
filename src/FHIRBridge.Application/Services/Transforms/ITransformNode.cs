using FHIRBridge.Domain.Enums;

namespace FHIRBridge.Application.Services.Transforms;

/// <summary>Outcome of one transform node execution — <see cref="Value"/> may be a scalar, a
/// <see cref="System.Text.Json.Nodes.JsonObject"/> (for the FHIR complex-type builder nodes), or null.</summary>
public sealed record TransformResult(bool Success, object? Value, string? Error)
{
    public static TransformResult Ok(object? value) => new(true, value, null);
    public static TransformResult Fail(string error) => new(false, null, error);
}

/// <summary>
/// One field-level, stateless FHIR-aware transform (FHIRBridge_Top20_Transformations.pdf). A value comes in,
/// a typed value goes out. Null-safety, array mode, and error-policy handling are the caller's
/// (<c>TransformNodeExecutor</c>'s) responsibility so each node only implements its own core conversion logic.
/// <paramref name="secret"/> is only populated for nodes that need a keyed secret (hashing, date-shift) —
/// it is resolved from the vault by the caller and never persisted in <see cref="Domain.Entities.TransformationRule.ConfigJson"/>.
/// </summary>
public interface ITransformNode
{
    TransformNodeType NodeType { get; }

    TransformResult Execute(object? value, IReadOnlyDictionary<string, string> config, string? secret);
}

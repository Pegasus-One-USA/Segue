using FHIRBridge.Domain.Enums;

namespace FHIRBridge.Application.Services.Transforms;

/// <summary>Outcome of one transform node execution — <see cref="Value"/> may be a scalar, a
/// <see cref="System.Text.Json.Nodes.JsonObject"/> (for the FHIR complex-type builder nodes), or null.
/// <paramref name="ResolvedSystemOverride"/> is set only by <see cref="Nodes.CodeableConceptBuilderNode"/>'s
/// cross-system auto-detect: when a code isn't found under the rule's configured system but IS found under a
/// different locally-synced one, this carries that actual system URI back to the caller so it can be recorded
/// on the lineage hop — surfacing the substitution instead of silently masking a misconfigured "system".</summary>
public sealed record TransformResult(bool Success, object? Value, string? Error, string? ResolvedSystemOverride = null)
{
    public static TransformResult Ok(object? value, string? resolvedSystemOverride = null) => new(true, value, null, resolvedSystemOverride);
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

    /// <summary>Async counterpart of <see cref="Execute"/> — only overridden by a node whose transformation
    /// genuinely needs an await (e.g. a DB-backed terminology lookup); every other node gets this for free via
    /// the default body, so adding one async node never forces the other ~20 to change.</summary>
    Task<TransformResult> ExecuteAsync(
        object? value, IReadOnlyDictionary<string, string> config, string? secret, CancellationToken cancellationToken = default)
        => Task.FromResult(Execute(value, config, secret));
}

using FHIRBridge.Domain.Entities;
using FHIRBridge.Domain.Enums;

namespace FHIRBridge.Application.Services.Transforms;

/// <summary>
/// Enforces <see cref="TransformationRule.OnNull"/> — previously stored on every rule but never consulted
/// anywhere. Checked BEFORE a node runs, not after: a node whose input is missing never executes at all when
/// this fires; instead the chain gets whatever <see cref="Apply"/> returns.
/// </summary>
public static class TransformNullPolicy
{
    /// <summary>Matches the null-check every node already applies to its own input (null, or a
    /// whitespace-only string) — kept in one place so the pre-execution check and each node's own guard
    /// agree on what "missing" means.</summary>
    public static bool IsNullOrEmpty(object? value) =>
        value is null || (value is string s && string.IsNullOrWhiteSpace(s));

    /// <summary>Returns the value the chain should carry forward, and whether the chain must stop
    /// (mirrors the existing error-policy short-circuit for <see cref="TransformErrorPolicy.Fail"/>/
    /// <see cref="TransformErrorPolicy.RouteToDeadLetter"/>).</summary>
    public static object? Apply(TransformationRule rule, object? currentValue, out bool stopChain)
    {
        switch (rule.OnNull)
        {
            case NullPolicy.Default:
                stopChain = false;
                return rule.OnNullDefaultValue;

            case NullPolicy.Error:
                stopChain = rule.ErrorPolicy is TransformErrorPolicy.Fail or TransformErrorPolicy.RouteToDeadLetter;
                return rule.ErrorPolicy == TransformErrorPolicy.PassThrough ? currentValue : null;

            default: // Skip — leave the node out of the chain, value stays as-is (missing).
                stopChain = false;
                return currentValue;
        }
    }
}

using System.Text.Json;

namespace FHIRBridge.Runtime.Domain.Workflows;

/// <summary>
/// Resolves which part of a node's <see cref="WorkflowNode.ConfigurationJson"/> holds its settings, so readers
/// work against both the legacy flat shape and the enveloped shape without caring which one they were given.
///
/// <para><b>Legacy (flat)</b> — settings sit at the root, alongside ids pointing at master records:
/// <c>{ "sourceConnectionId": "…", "resourceTypes": [ … ] }</c></para>
///
/// <para><b>Enveloped</b> — settings move under <c>config</c>, and <c>ref</c> carries provenance (which master
/// record the settings were copied from, and at which version) for the portal's "master has changed, pull
/// update?" affordance. Nothing in the engine reads <c>ref</c>:
/// <c>{ "ref": { "masterId": "…", "masterVersion": 7 }, "config": { "baseUrl": "…" } }</c></para>
///
/// Detection is by shape, not by a flag, so no migration has to run before readers can cope: a root object with
/// a <c>config</c> object is enveloped, anything else is read exactly as before. That is what lets the migration
/// (plan §7) be verified on real data while the old shape still works.
///
/// See docs/backend/18-workflow-self-contained-config-plan.md §3.
/// </summary>
public static class WorkflowNodeConfigurationEnvelope
{
    /// <summary>The property holding the node's actual settings in the enveloped shape.</summary>
    public const string ConfigProperty = "config";

    /// <summary>The property holding copied-from provenance. Portal-only — never read by executors.</summary>
    public const string RefProperty = "ref";

    /// <summary>
    /// The element a reader should look up its settings in: the <c>config</c> object when this node is
    /// enveloped, otherwise the root itself. Returns false when the JSON is absent, malformed, or not an
    /// object, so callers fall through to their existing "no configuration" path rather than throwing.
    /// </summary>
    /// <remarks>
    /// The returned element belongs to <paramref name="document"/>; it is only valid while that document is
    /// alive, so callers must keep it in scope (JsonDocument is IDisposable and pools its buffers).
    /// </remarks>
    public static bool TryGetSettings(JsonDocument? document, out JsonElement settings)
    {
        settings = default;
        if (document is null || document.RootElement.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        settings = ResolveSettings(document.RootElement);
        return true;
    }

    /// <summary>
    /// Element-level counterpart to <see cref="TryGetSettings"/>, for callers that already hold a parsed root.
    /// A non-object root is returned unchanged — the caller's own shape checks still apply.
    /// </summary>
    public static JsonElement ResolveSettings(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return root;
        }

        // Only an OBJECT under "config" counts. A legacy node with a scalar property that happens to be called
        // "config" (a wizard field bag is Record<string,string>, so such a value would be a string) must keep
        // being read as flat, or its real settings would disappear behind an empty envelope.
        return root.TryGetProperty(ConfigProperty, out var config) && config.ValueKind == JsonValueKind.Object
            ? config
            : root;
    }

    /// <summary>Whether this node has been migrated to the enveloped shape. Intended for migration reporting
    /// and diagnostics; executors should call <see cref="ResolveSettings"/> and stay agnostic.</summary>
    public static bool IsEnveloped(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object
        && root.TryGetProperty(ConfigProperty, out var config)
        && config.ValueKind == JsonValueKind.Object;
}

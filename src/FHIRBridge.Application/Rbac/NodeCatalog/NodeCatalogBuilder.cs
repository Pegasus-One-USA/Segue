using FHIRBridge.Application.DTOs;
using FHIRBridge.Application.Security;
using FHIRBridge.Domain.Enums;

namespace FHIRBridge.Application.Rbac.NodeCatalog;

/// <summary>
/// Assembles the canonical Node Catalog: one <see cref="NodeCatalogEntryDto"/> per
/// <see cref="SourceSystemType"/>/<see cref="DestinationType"/> value, merging (1) the type's own
/// identity, (2) whichever permission group it resolves to (if any) via the existing
/// <see cref="SourceSystemPermissionGroups"/> name-matching, with that group's actual permissions
/// pulled from the already-fetched permission catalog — the very same data
/// <c>/api/v1/permissions/catalog</c> already returns, so this never disagrees with it — and (3) the
/// hand-authored <see cref="NodeCatalogMetadata"/> registry. A pure function: no I/O of its own.
/// </summary>
public static class NodeCatalogBuilder
{
    // Legacy actions preserved verbatim on Epic/Athenahealth/Cerner from a pre-registry seed block
    // (see RbacSeedData.Permissions) — superseded by the real View/Edit/Execute actions those vendors
    // get through normal discovery. Never surfaced as a node action, same as the frontend-only filter
    // this replaces (permission-matrix.config.ts's former EXCLUDED_NODE_ACTIONS).
    private static readonly HashSet<string> ExcludedActions = new(StringComparer.OrdinalIgnoreCase) { "read", "assign" };

    public static IReadOnlyList<NodeCatalogEntryDto> Build(IReadOnlyList<PermissionCatalogCategoryDto> permissionCatalog)
    {
        var pipelinesGroups = permissionCatalog
            .FirstOrDefault(c => string.Equals(c.Name, nameof(PermissionCategoryCode.Pipelines), StringComparison.Ordinal))
            ?.Groups ?? Array.Empty<PermissionCatalogGroupDto>();

        var groupsByName = pipelinesGroups.ToDictionary(g => g.Name, StringComparer.Ordinal);

        var entries = new List<NodeCatalogEntryDto>();

        foreach (var value in Enum.GetValues<SourceSystemType>())
        {
            if (!NodeCatalogMetadata.TryGetSource(value, out var metadata))
            {
                // NodeCatalogMetadata.ValidateCompleteness() already fails the boot for this — this is
                // a defensive second check for direct/unit-test callers that bypass startup.
                throw MissingMetadataException("SourceSystemType", value.ToString());
            }

            entries.Add(BuildEntry("Source", value.ToString(), value, groupsByName, metadata));
        }

        foreach (var value in Enum.GetValues<DestinationType>())
        {
            if (!NodeCatalogMetadata.TryGetDestination(value, out var metadata))
            {
                throw MissingMetadataException("DestinationType", value.ToString());
            }

            entries.Add(BuildEntry("Destination", value.ToString(), value, groupsByName, metadata));
        }

        return entries;
    }

    private static NodeCatalogEntryDto BuildEntry(
        string kind,
        string type,
        Enum enumValue,
        IReadOnlyDictionary<string, PermissionCatalogGroupDto> groupsByName,
        NodeCatalogMetadata.Entry metadata)
    {
        string? permissionGroupName = null;
        IReadOnlyList<NodeCatalogActionDto> actions = Array.Empty<NodeCatalogActionDto>();

        if (SourceSystemPermissionGroups.TryGroupFor(enumValue, out var group)
            && groupsByName.TryGetValue(group.ToString(), out var catalogGroup))
        {
            permissionGroupName = group.ToString();
            actions = catalogGroup.Permissions
                .Where(p => !ExcludedActions.Contains(ActionSuffix(p.Name)))
                .Select(p => new NodeCatalogActionDto(TitleCase(ActionSuffix(p.Name)), p.Name))
                .ToArray();
        }

        return new NodeCatalogEntryDto(
            kind,
            type,
            metadata.DisplayName,
            metadata.Subtitle,
            metadata.Category,
            metadata.Icon,
            metadata.Color,
            metadata.Implemented,
            permissionGroupName,
            actions);
    }

    private static string ActionSuffix(string permissionCode)
    {
        var dot = permissionCode.LastIndexOf('.');
        return dot < 0 ? permissionCode : permissionCode[(dot + 1)..];
    }

    private static string TitleCase(string action) =>
        action.Length == 0 ? action : char.ToUpperInvariant(action[0]) + action[1..];

    private static InvalidOperationException MissingMetadataException(string enumName, string value) =>
        new($"NodeCatalogMetadata has no entry for {enumName}.{value} — refusing to build an incomplete " +
            "Node Catalog entry. Add one to NodeCatalogMetadata first.");
}

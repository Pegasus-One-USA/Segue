namespace FHIRBridge.Application.DTOs;

/// <summary>
/// One action a node exposes — mirrors exactly one permission (e.g. "View" → "epic.view"). The same
/// wire code returned by <see cref="PermissionDto.Name"/> in <c>/api/v1/permissions/catalog</c>, so a
/// client that already resolves that code to a Permission Id for save/toggle purposes (Role
/// Permissions) needs no new resolution path, and a client that only ever calls <c>hasPermission(code)</c>
/// (Node Library) needs no separate prefix-building logic either.
/// </summary>
public sealed record NodeCatalogActionDto(string Action, string Code);

/// <summary>
/// One source vendor or destination type node — the single canonical representation shared by the
/// Workflow Builder Node Library and the Role Permissions "Workflow Nodes" section. See
/// <c>NodeCatalogBuilder</c> for how this is assembled and <c>NodeCatalogMetadata</c> for the one
/// hand-authored registry backing <see cref="DisplayName"/>/<see cref="Category"/>/etc.
/// </summary>
public sealed record NodeCatalogEntryDto(
    /// <summary>"Source" or "Destination" — which enum this node came from.</summary>
    string Kind,
    /// <summary>
    /// The canonical identifier: the exact <c>SourceSystemType</c>/<c>DestinationType</c> enum member
    /// name (e.g. "Epic", "FhirRepository", "Medplum") — never a hand-typed id, never a separate
    /// permission-prefix string. This is the same string already used on the wire for that enum
    /// elsewhere in the app (e.g. a create-destination request body's own <c>DestinationType</c> value).
    /// </summary>
    string Type,
    string DisplayName,
    string? Subtitle,
    string? Category,
    string? Icon,
    string? Color,
    /// <summary>
    /// Whether this node has a real, working implementation reachable end-to-end from the Workflow
    /// Builder — a permanent technical fact, not a phase/rollout decision. Replaces
    /// PhaseConfigService's hardcoded id arrays. See <c>NodeCatalogMetadata.Entry.Implemented</c> for
    /// the exact bar a type must clear before this is <see langword="true"/>.
    /// </summary>
    bool Implemented,
    /// <summary>
    /// The <c>PermissionGroupCode</c> member name governing this node, or <see langword="null"/> if this
    /// type has no dedicated group (falls back to the generic ungated path — see
    /// <c>ControllerAuthorizationExtensions.HasPermissionAsync</c>). Never "SourceConnections": that
    /// fallback name is deliberately never surfaced as a node's own permission group, since it isn't
    /// one — see the investigation this catalog was scoped from for why that substitution is wrong.
    /// </summary>
    string? PermissionGroup,
    /// <summary>
    /// This node's own View/Create/Edit/Delete/Execute permissions, in that order — empty when
    /// <see cref="PermissionGroup"/> is null. Legacy <c>.read</c>/<c>.assign</c> actions (Epic/
    /// Athenahealth/Cerner only) are deliberately excluded here, same as today's frontend-only filter,
    /// now enforced once, server-side.
    /// </summary>
    IReadOnlyList<NodeCatalogActionDto> Actions);

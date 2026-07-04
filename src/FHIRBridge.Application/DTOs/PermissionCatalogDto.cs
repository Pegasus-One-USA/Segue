namespace FHIRBridge.Application.DTOs;

/// <summary>A permission group and the (visible) permissions within it, for the permission-management grid.</summary>
public sealed record PermissionCatalogGroupDto(
    Guid Id,
    string Name,
    string DisplayName,
    IReadOnlyList<PermissionDto> Permissions);

/// <summary>A permission category and its (visible) groups, for the permission-management grid.</summary>
public sealed record PermissionCatalogCategoryDto(
    Guid Id,
    string Name,
    string DisplayName,
    IReadOnlyList<PermissionCatalogGroupDto> Groups);

namespace FHIRBridge.Application.Security;

/// <summary>
/// The four built-in platform roles. Role names are used verbatim as JWT role claims and
/// persisted role names, so they stay as space-free identifiers; friendly text lives in the
/// role description seed.
/// </summary>
public static class UnifiedRoles
{
    /// <summary>Full platform administrator across all tenants.</summary>
    public const string SuperAdmin = nameof(SuperAdmin);

    /// <summary>Administers configuration and users within a tenant.</summary>
    public const string Admin = nameof(Admin);

    /// <summary>Builds and runs pipeline configurations, and reviews data and audit output, within a tenant.</summary>
    public const string Operations = nameof(Operations);

    /// <summary>Read-only access to configuration and audit logs.</summary>
    public const string Audit = nameof(Audit);
}

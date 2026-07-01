namespace FHIRBridge.Application.Security;

/// <summary>
/// The five built-in platform roles. Role names are used verbatim as JWT role claims and
/// persisted role names, so they stay as space-free identifiers; friendly text lives in the
/// role description seed.
/// </summary>
public static class UnifiedRoles
{
    /// <summary>Full platform administrator across all tenants.</summary>
    public const string GlobalAdmin = nameof(GlobalAdmin);

    /// <summary>Administers configuration and users within a tenant.</summary>
    public const string TenantAdmin = nameof(TenantAdmin);

    /// <summary>Builds and runs pipeline configurations within a tenant.</summary>
    public const string PipelineEngineer = nameof(PipelineEngineer);

    /// <summary>Runs pipelines and reviews data and audit output.</summary>
    public const string Analyst = nameof(Analyst);

    /// <summary>Read-only access to configuration and audit logs.</summary>
    public const string Auditor = nameof(Auditor);
}

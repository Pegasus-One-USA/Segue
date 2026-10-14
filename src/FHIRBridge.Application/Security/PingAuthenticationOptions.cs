namespace FHIRBridge.Application.Security;

/// <summary>
/// Binds the <c>Authentication:Ping</c> configuration section. PingOne single sign-on is OFF by default.
/// When enabled, Ping is also the source of truth for RBAC: at every SSO login the user's PingOne group
/// membership is fetched live from the PingOne Management API and mapped onto FHIRBridge roles through
/// <see cref="GroupRoleMappings"/>.
/// </summary>
public sealed class PingAuthenticationOptions
{
    public bool Enabled { get; set; }

    /// <summary>PingOne environment id (GUID).</summary>
    public string? EnvironmentId { get; set; }

    /// <summary>PingOne authorization host, e.g. <c>auth.pingone.com</c> (region-specific).</summary>
    public string AuthDomain { get; set; } = "auth.pingone.com";

    /// <summary>PingOne Management API host, e.g. <c>api.pingone.com</c> (region-specific).</summary>
    public string ApiDomain { get; set; } = "api.pingone.com";

    /// <summary>Client id of the portal's OIDC application; the expected <c>aud</c> of the ID token.</summary>
    public string? ClientId { get; set; }

    /// <summary>Client id of the PingOne worker application used for Management API calls.</summary>
    public string? WorkerClientId { get; set; }

    /// <summary>Key Vault name holding the worker application's client secret (never stored inline).</summary>
    public string? WorkerClientSecretKeyVaultName { get; set; }

    /// <summary>Key Vault secret name of the worker application's client secret.</summary>
    public string? WorkerClientSecretName { get; set; }

    /// <summary>
    /// Maps a PingOne group id (or name) to one or more FHIRBridge role names. Matching is
    /// case-insensitive; see <see cref="EntraGroupRoleMapper"/>.
    /// </summary>
    public Dictionary<string, string[]> GroupRoleMappings { get; set; } = new();

    /// <summary>When true, a Ping user with no FHIRBridge account is created on first login, provided at
    /// least one of their Ping groups maps to a FHIRBridge role.</summary>
    public bool JitProvisioning { get; set; } = true;

    /// <summary>When true, a SAML login is also treated as a Ping login for role resolution (the user is
    /// located in PingOne by email).</summary>
    public bool ApplyToSaml { get; set; } = true;

    /// <summary>What to do when the role Ping maps differs from the role the user holds in Segue.</summary>
    public Abstractions.Security.ExternalRoleSyncMode RoleSyncMode { get; set; } = Abstractions.Security.ExternalRoleSyncMode.AutoSwitch;

    /// <summary>What to do when a user's groups map to several different roles.</summary>
    public Abstractions.Security.ExternalRoleConflictMode ConflictMode { get; set; } = Abstractions.Security.ExternalRoleConflictMode.Deny;

    /// <summary>What to do when none of a user's groups map to a Segue role.</summary>
    public Abstractions.Security.ExternalNoRoleMode NoMappedGroupMode { get; set; } = Abstractions.Security.ExternalNoRoleMode.Deny;
}

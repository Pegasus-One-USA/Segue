namespace FHIRBridge.Application.Security;

/// <summary>
/// Binds the <c>Authentication:Entra</c> configuration section. Microsoft Entra ID single
/// sign-on is OFF by default; local JWT sign-in keeps working unchanged until <see cref="Enabled"/>
/// is set. When enabled, the API validates Entra-issued bearer tokens alongside local tokens and
/// projects Entra security-group membership onto the five built-in FHIRBridge roles.
/// </summary>
public sealed class EntraAuthenticationOptions
{
    /// <summary>When false (default) only local JWT bearer tokens are accepted.</summary>
    public bool Enabled { get; set; }

    /// <summary>Entra login instance, e.g. <c>https://login.microsoftonline.com/</c>.</summary>
    public string Instance { get; set; } = "https://login.microsoftonline.com/";

    /// <summary>Directory (tenant) id, or <c>common</c>/<c>organizations</c> for multi-tenant.</summary>
    public string? TenantId { get; set; }

    /// <summary>Application (client) id of the API app registration.</summary>
    public string? ClientId { get; set; }

    /// <summary>
    /// Optional explicit audience. When unset, Microsoft.Identity.Web accepts the client id and
    /// <c>api://{clientId}</c> by default.
    /// </summary>
    public string? Audience { get; set; }

    /// <summary>
    /// Maps an Entra security-group object id (or display name, depending on how the token's
    /// <c>groups</c> claim is emitted) to one or more <see cref="UnifiedRoles"/> role names.
    /// </summary>
    public Dictionary<string, string[]> GroupRoleMappings { get; set; } = new();
}

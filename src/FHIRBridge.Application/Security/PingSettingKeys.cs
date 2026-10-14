using FHIRBridge.Domain.ValueObjects;

namespace FHIRBridge.Application.Security;

/// <summary>
/// System-setting keys (and the fixed secret reference) for the PingOne settings saved from the SSO Configurations
/// screen. One definition shared by the screen's service and the runtime settings reader, so they cannot drift.
/// </summary>
public static class PingSettingKeys
{
    /// <summary>Fixed reference for the worker secret when it was entered on the SSO Configurations screen.</summary>
    public static readonly SecretReference UiWorkerSecretReference = new("app", "ping-worker-client-secret");

    public const string Enabled = "Authentication:Ping:Enabled";
    public const string EnvironmentId = "Authentication:Ping:EnvironmentId";
    public const string AuthDomain = "Authentication:Ping:AuthDomain";
    public const string ApiDomain = "Authentication:Ping:ApiDomain";
    public const string ClientId = "Authentication:Ping:ClientId";
    public const string WorkerClientId = "Authentication:Ping:WorkerClientId";
    public const string WorkerSecretConfigured = "Authentication:Ping:WorkerSecretConfigured";
    public const string JitProvisioning = "Authentication:Ping:JitProvisioning";
    public const string ApplyToSaml = "Authentication:Ping:ApplyToSaml";
    public const string GroupRoleMappings = "Authentication:Ping:GroupRoleMappings";
    public const string RoleSyncMode = "Authentication:Ping:RoleSyncMode";
    public const string ConflictMode = "Authentication:Ping:ConflictMode";
    public const string NoMappedGroupMode = "Authentication:Ping:NoMappedGroupMode";
}

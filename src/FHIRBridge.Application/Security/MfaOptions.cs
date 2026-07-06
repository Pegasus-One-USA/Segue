namespace FHIRBridge.Application.Security;

public sealed class MfaOptions
{
    /// <summary>Issuer label shown in the authenticator app (appears above the code).</summary>
    public string Issuer { get; set; } = "FHIRBridge";

    /// <summary>Number of one-time backup codes generated at enrollment.</summary>
    public int BackupCodeCount { get; set; } = 10;
}

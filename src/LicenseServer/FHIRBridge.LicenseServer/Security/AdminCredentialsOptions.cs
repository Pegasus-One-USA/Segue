namespace FHIRBridge.LicenseServer.Security;

/// <summary>
/// Bound from the "AdminCredentials" configuration section. A single shared admin account — this is a
/// small internal tool, not a multi-user system; see README.md for the explicit trade-off note about
/// upgrading this before it handles real customer data.
/// </summary>
public sealed class AdminCredentialsOptions
{
    public const string SectionName = "AdminCredentials";

    /// <summary>Defaults to "admin" if not configured.</summary>
    public string? Username { get; set; }

    /// <summary>
    /// If left blank, <see cref="AdminAccountProvider"/> generates a random password once at startup and
    /// logs it clearly — set this (via environment variable, e.g. <c>AdminCredentials__Password</c>, or a
    /// secrets store) to pin a real password across restarts.
    /// </summary>
    public string? Password { get; set; }
}

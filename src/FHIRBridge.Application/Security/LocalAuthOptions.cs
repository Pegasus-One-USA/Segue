namespace FHIRBridge.Application.Security;

public sealed class LocalAuthOptions
{
    /// <summary>URL template for password-reset emails; supports {token} and {email} placeholders.</summary>
    public string? PasswordResetUrlTemplate { get; set; }

    /// <summary>URL template for invitation emails; supports {token} and {email} placeholders.</summary>
    public string? AcceptInviteUrlTemplate { get; set; }

    /// <summary>Account-lockout policy for local password login (brute-force protection).</summary>
    public LockoutOptions Lockout { get; set; } = new();
}

public sealed class LockoutOptions
{
    /// <summary>Consecutive failed logins that trigger a lockout.</summary>
    public int MaxFailedAttempts { get; set; } = 5;

    /// <summary>How long the account stays locked once the threshold is reached.</summary>
    public int LockoutMinutes { get; set; } = 15;
}

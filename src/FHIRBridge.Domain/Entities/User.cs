using FHIRBridge.SharedKernel.Abstractions;

namespace FHIRBridge.Domain.Entities;

public sealed class User : AuditableChildEntity<Guid>
{
    private User()
    {
    }

    public User(
        string externalUserId,
        string? email,
        string? displayName)
    {
        Id = Guid.NewGuid();
        ExternalUserId = externalUserId;
        Email = email;
        DisplayName = displayName;
        IsEnabled = true;
        IsLocalLoginEnabled = false;
        MustChangePassword = false;
    }

    public string ExternalUserId { get; private set; } = default!;
    public string? Email { get; private set; }
    public string? DisplayName { get; private set; }

    /// <summary>Optional structured given name (complements the single <see cref="DisplayName"/>).</summary>
    public string? FirstName { get; private set; }

    /// <summary>Optional structured family name.</summary>
    public string? LastName { get; private set; }

    /// <summary>
    /// Optional "home"/primary tenant. Does not replace <c>TenantUsers</c> membership (which still carries
    /// cross-tenant membership + per-tenant roles); null for platform users such as GlobalAdmin.
    /// </summary>
    public Guid? TenantId { get; private set; }

    public string? PasswordHash { get; private set; }
    public bool IsLocalLoginEnabled { get; private set; }
    public bool MustChangePassword { get; private set; }
    public string? PasswordResetTokenHash { get; private set; }
    public DateTime? PasswordResetTokenExpiresOnUtc { get; private set; }

    /// <summary>Account enable/disable. When false the user cannot authenticate (renamed from IsActive).</summary>
    public bool IsEnabled { get; private set; }
    public DateTime? LastLoginOnUtc { get; private set; }

    // Security hardening columns.
    public int FailedLoginCount { get; private set; }
    public DateTime? LockoutEndUtc { get; private set; }
    public DateTime? LastPasswordChangedOnUtc { get; private set; }
    public DateTime? PasswordExpiresOnUtc { get; private set; }
    public bool MfaEnabled { get; private set; }

    /// <summary>True when the account is currently locked out (failed-login threshold reached).</summary>
    public bool IsLockedOut(DateTime utcNow) => LockoutEndUtc is { } end && end > utcNow;

    public void UpdateProfile(string? email, string? displayName)
    {
        Email = email;
        DisplayName = displayName;
    }

    public void UpdateName(string? firstName, string? lastName)
    {
        FirstName = firstName;
        LastName = lastName;
    }

    public void SetHomeTenant(Guid? tenantId)
    {
        TenantId = tenantId;
    }

    public void UpdateExternalUserId(string externalUserId)
    {
        ExternalUserId = externalUserId;
    }

    public void SetEnabled(bool isEnabled)
    {
        IsEnabled = isEnabled;
    }

    public void SetMfaEnabled(bool mfaEnabled)
    {
        MfaEnabled = mfaEnabled;
    }

    public void EnableLocalLogin(string passwordHash, bool mustChangePassword)
    {
        PasswordHash = passwordHash;
        IsLocalLoginEnabled = true;
        MustChangePassword = mustChangePassword;
        LastPasswordChangedOnUtc = DateTime.UtcNow;
    }

    public void SetPassword(string passwordHash, bool mustChangePassword)
    {
        PasswordHash = passwordHash;
        IsLocalLoginEnabled = true;
        MustChangePassword = mustChangePassword;
        PasswordResetTokenHash = null;
        PasswordResetTokenExpiresOnUtc = null;
        LastPasswordChangedOnUtc = DateTime.UtcNow;
        FailedLoginCount = 0;
        LockoutEndUtc = null;
    }

    public void SetPasswordResetToken(string resetTokenHash, DateTime expiresOnUtc)
    {
        PasswordResetTokenHash = resetTokenHash;
        PasswordResetTokenExpiresOnUtc = expiresOnUtc;
    }

    public void ClearPasswordResetToken()
    {
        PasswordResetTokenHash = null;
        PasswordResetTokenExpiresOnUtc = null;
    }

    /// <summary>Records a failed login attempt, locking the account once the threshold is reached.</summary>
    public void RegisterFailedLogin(int maxAttempts, TimeSpan lockoutDuration)
    {
        FailedLoginCount++;
        if (FailedLoginCount >= maxAttempts)
        {
            LockoutEndUtc = DateTime.UtcNow.Add(lockoutDuration);
        }
    }

    public void ResetFailedLogins()
    {
        FailedLoginCount = 0;
        LockoutEndUtc = null;
    }

    public void RecordLogin()
    {
        LastLoginOnUtc = DateTime.UtcNow;
        FailedLoginCount = 0;
        LockoutEndUtc = null;
    }
}

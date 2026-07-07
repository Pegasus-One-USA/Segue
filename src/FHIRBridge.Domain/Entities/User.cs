using FHIRBridge.Domain.Enums;
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
        Status = UserStatus.Active;
        IsEnabled = true;
        IsLocalLoginEnabled = false;
        MustChangePassword = false;
        LoginProvider = LoginProvider.Local;
    }

    public string ExternalUserId { get; private set; } = default!;
    public string? Email { get; private set; }
    public string? DisplayName { get; private set; }

    public string? FirstName { get; private set; }
    public string? LastName { get; private set; }

    public string? PasswordHash { get; private set; }
    public bool IsLocalLoginEnabled { get; private set; }
    public bool MustChangePassword { get; private set; }
    public string? PasswordResetTokenHash { get; private set; }
    public DateTime? PasswordResetTokenExpiresOnUtc { get; private set; }

    /// <summary>Account status: Active, Inactive, or Invited.</summary>
    public UserStatus Status { get; private set; }

    /// <summary>Which identity provider authenticates this user (Local password, Entra, or Google).</summary>
    public LoginProvider LoginProvider { get; private set; } = LoginProvider.Local;

    /// <summary>Account enable/disable. When false the user cannot authenticate.</summary>
    public bool IsEnabled { get; private set; }
    public DateTime? LastLoginOnUtc { get; private set; }

    // Security hardening columns.
    public int FailedLoginCount { get; private set; }
    public DateTime? LockoutEndUtc { get; private set; }
    public DateTime? LastPasswordChangedOnUtc { get; private set; }
    public DateTime? PasswordExpiresOnUtc { get; private set; }
    public bool MfaEnabled { get; private set; }

    /// <summary>Base32 TOTP shared secret. Staged during enrollment, active once <see cref="MfaEnabled"/> is true.</summary>
    public string? MfaSecret { get; private set; }

    /// <summary>Newline-separated hashes of the user's remaining one-time MFA backup codes.</summary>
    public string? MfaBackupCodeHashes { get; private set; }

    /// <summary>When the user last completed MFA enrollment (confirmed a code against the staged secret).</summary>
    public DateTime? MfaEnrolledOnUtc { get; private set; }

    // Invitation flow fields.
    public string? InvitationTokenHash { get; private set; }
    public DateTime? InvitationTokenExpiresOnUtc { get; private set; }

    // Refresh token fields.
    public string? RefreshTokenHash { get; private set; }
    public DateTime? RefreshTokenExpiresOnUtc { get; private set; }

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

    public void UpdateExternalUserId(string externalUserId)
    {
        ExternalUserId = externalUserId;
    }

    public void SetEnabled(bool isEnabled)
    {
        IsEnabled = isEnabled;
        Status = isEnabled ? UserStatus.Active : UserStatus.Inactive;
    }

    public void SetMfaEnabled(bool mfaEnabled)
    {
        MfaEnabled = mfaEnabled;
    }

    /// <summary>Stages a TOTP secret for enrollment. MFA is not enforced until <see cref="ConfirmMfaEnrollment"/>.</summary>
    public void BeginMfaEnrollment(string secret)
    {
        if (string.IsNullOrWhiteSpace(secret))
        {
            throw new InvalidOperationException("An MFA secret is required to begin enrollment.");
        }

        MfaSecret = secret;
        MfaEnabled = false;
        MfaEnrolledOnUtc = null;
    }

    /// <summary>Activates MFA after the user proves possession of the staged secret; stores the backup-code hashes.</summary>
    public void ConfirmMfaEnrollment(IEnumerable<string> backupCodeHashes)
    {
        if (string.IsNullOrWhiteSpace(MfaSecret))
        {
            throw new InvalidOperationException("MFA enrollment has not been started.");
        }

        MfaEnabled = true;
        MfaEnrolledOnUtc = DateTime.UtcNow;
        MfaBackupCodeHashes = string.Join('\n', backupCodeHashes);
    }

    /// <summary>Fully disables MFA and clears the secret and backup codes.</summary>
    public void DisableMfa()
    {
        MfaEnabled = false;
        MfaSecret = null;
        MfaBackupCodeHashes = null;
        MfaEnrolledOnUtc = null;
    }

    /// <summary>
    /// Consumes a one-time backup code if its hash is present, removing it so it cannot be reused.
    /// Returns true when a matching unused code was found and consumed.
    /// </summary>
    public bool TryConsumeBackupCode(string backupCodeHash)
    {
        if (string.IsNullOrEmpty(MfaBackupCodeHashes) || string.IsNullOrEmpty(backupCodeHash))
        {
            return false;
        }

        var remaining = MfaBackupCodeHashes
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        if (!remaining.Remove(backupCodeHash))
        {
            return false;
        }

        MfaBackupCodeHashes = remaining.Count == 0 ? null : string.Join('\n', remaining);
        return true;
    }

    public void EnableLocalLogin(string passwordHash, bool mustChangePassword)
    {
        PasswordHash = passwordHash;
        IsLocalLoginEnabled = true;
        MustChangePassword = mustChangePassword;
        LastPasswordChangedOnUtc = DateTime.UtcNow;
        Status = UserStatus.Active;
        IsEnabled = true;
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

    /// <summary>Marks the user as invited, storing the hashed invitation token.</summary>
    public void SetInvited(string invitationTokenHash, DateTime expiresOnUtc)
    {
        Status = UserStatus.Invited;
        IsEnabled = false;
        IsLocalLoginEnabled = false;
        InvitationTokenHash = invitationTokenHash;
        InvitationTokenExpiresOnUtc = expiresOnUtc;
    }

    /// <summary>Accepts an invitation: sets the password, activates the account, and clears the invitation token.</summary>
    public void AcceptInvitation(string passwordHash, string? firstName, string? lastName)
    {
        Status = UserStatus.Active;
        IsEnabled = true;
        IsLocalLoginEnabled = true;
        MustChangePassword = false;
        PasswordHash = passwordHash;
        LastPasswordChangedOnUtc = DateTime.UtcNow;
        FailedLoginCount = 0;
        LockoutEndUtc = null;
        InvitationTokenHash = null;
        InvitationTokenExpiresOnUtc = null;
        if (firstName is not null) FirstName = firstName;
        if (lastName is not null) LastName = lastName;
    }

    /// <summary>
    /// Links this account to an external identity provider (Entra/Google). Local password login is
    /// disabled: the user now authenticates by exchanging an external IdP token for a FHIRBridge JWT.
    /// </summary>
    public void LinkExternalIdentity(string externalUserId, LoginProvider provider)
    {
        if (string.IsNullOrWhiteSpace(externalUserId))
        {
            throw new InvalidOperationException("External user id is required to link an external identity.");
        }

        ExternalUserId = externalUserId;
        LoginProvider = provider;
        IsLocalLoginEnabled = false;
    }

    /// <summary>
    /// Accepts an invitation via an external identity provider: activates the account, links the external
    /// identity (no password), sets any provided names, and clears the invitation token.
    /// </summary>
    public void AcceptInvitationViaSso(
        string externalUserId,
        LoginProvider provider,
        string? firstName,
        string? lastName)
    {
        Status = UserStatus.Active;
        IsEnabled = true;
        MustChangePassword = false;
        LinkExternalIdentity(externalUserId, provider);
        InvitationTokenHash = null;
        InvitationTokenExpiresOnUtc = null;
        if (firstName is not null) FirstName = firstName;
        if (lastName is not null) LastName = lastName;
    }

    /// <summary>Stores a hashed refresh token for the 30-day refresh flow.</summary>
    public void SetRefreshToken(string refreshTokenHash, DateTime expiresOnUtc)
    {
        RefreshTokenHash = refreshTokenHash;
        RefreshTokenExpiresOnUtc = expiresOnUtc;
    }

    /// <summary>Clears the refresh token, effectively logging the user out of the refresh flow.</summary>
    public void ClearRefreshToken()
    {
        RefreshTokenHash = null;
        RefreshTokenExpiresOnUtc = null;
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

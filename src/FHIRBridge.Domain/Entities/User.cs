using FHIRBridge.Domain.Enums;
using FHIRBridge.SharedKernel.Abstractions;

namespace FHIRBridge.Domain.Entities;

public sealed class User : AuditableChildEntity<Guid>, IHasAuditDisplayName
{
    private User()
    {
    }

    public User(
        string externalUserId,
        string? email,
        string? displayName,
        Guid tenantId)
    {
        Id = Guid.NewGuid();
        ExternalUserId = externalUserId;
        Email = email;
        DisplayName = displayName;
        TenantId = tenantId;
        Status = UserStatus.Active;
        IsEnabled = true;
        IsLocalLoginEnabled = false;
        MustChangePassword = false;
        LoginProvider = LoginProvider.Local;
    }

    public string ExternalUserId { get; private set; } = default!;
    public string? Email { get; private set; }
    public string? DisplayName { get; private set; }
    string? IHasAuditDisplayName.AuditDisplayName => DisplayName ?? Email;

    /// <summary>The Tenant (customer/company) this user belongs to — required. There is no user↔tenant
    /// membership change API today; every user is assigned once, at creation, from the caller's own
    /// tenant (or the Default Tenant for first-run/JIT-provisioned users) — see UserManagementService,
    /// SetupService, UserAccessService.</summary>
    public Guid TenantId { get; private set; }

    public string? FirstName { get; private set; }
    public string? LastName { get; private set; }

    /// <summary>Falls back to "FirstName LastName" (then Email) when no explicit DisplayName was ever set —
    /// e.g. an SSO-provisioned user gets FirstName/LastName from the IdP's claims but DisplayName is never
    /// populated by that path, unlike local invite/signup which always sets it.</summary>
    public string EffectiveDisplayName
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(DisplayName))
            {
                return DisplayName;
            }

            var composedName = $"{FirstName} {LastName}".Trim();
            if (!string.IsNullOrWhiteSpace(composedName))
            {
                return composedName;
            }

            return Email ?? ExternalUserId;
        }
    }

    public string? PasswordHash { get; private set; }
    public bool IsLocalLoginEnabled { get; private set; }
    public bool MustChangePassword { get; private set; }
    public string? PasswordResetTokenHash { get; private set; }
    public DateTime? PasswordResetTokenExpiresOnUtc { get; private set; }

    /// <summary>Hash of the short-lived, single-use passwordless "magic link" sign-in token.</summary>
    public string? MagicLinkTokenHash { get; private set; }
    public DateTime? MagicLinkTokenExpiresOnUtc { get; private set; }

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

    /// <summary>
    /// HIPAA #12: true if either an admin/self-service reset explicitly requires a change
    /// (<see cref="MustChangePassword"/>), or the configured rotation period has elapsed
    /// (<see cref="PasswordExpiresOnUtc"/>). Callers gating login/session behavior should read this, not
    /// <see cref="MustChangePassword"/> alone, so expiry-driven rotation reuses the same enforced flow.
    /// </summary>
    public bool RequiresPasswordChange =>
        MustChangePassword || (PasswordExpiresOnUtc is { } expiresOnUtc && expiresOnUtc < DateTime.UtcNow);

    /// <summary>Base32 TOTP shared secret. Staged during enrollment, active once <see cref="MfaEnabled"/> is true.</summary>
    public string? MfaSecret { get; private set; }

    /// <summary>Newline-separated hashes of the user's remaining one-time MFA backup codes.</summary>
    public string? MfaBackupCodeHashes { get; private set; }

    /// <summary>When the user last completed MFA enrollment (confirmed a code against the staged secret).</summary>
    public DateTime? MfaEnrolledOnUtc { get; private set; }

    /// <summary>Hash of the short-lived, single-use token issued after password verification when MFA is required to finish logging in.</summary>
    public string? MfaChallengeTokenHash { get; private set; }
    public DateTime? MfaChallengeExpiresOnUtc { get; private set; }

    /// <summary>
    /// Admin-set policy: this account must have MFA enabled. When true and <see cref="MfaEnabled"/>
    /// is false, login succeeds but the session is gated to MFA enrollment only (mirrors
    /// <see cref="MustChangePassword"/>'s gate) until the user completes enrollment.
    /// </summary>
    public bool MustSetupMfa { get; private set; }

    /// <summary>True while the account is blocked to MFA-enrollment-only: an admin requires MFA but
    /// it isn't enrolled yet. Computed once here rather than re-derived at each call site (the JWT
    /// claim and the login response both need this exact value and must never disagree).</summary>
    public bool IsMfaSetupRequired => MustSetupMfa && !MfaEnabled;

    // Invitation flow fields.
    public string? InvitationTokenHash { get; private set; }
    public DateTime? InvitationTokenExpiresOnUtc { get; private set; }

    // Refresh token fields.
    public string? RefreshTokenHash { get; private set; }
    public DateTime? RefreshTokenExpiresOnUtc { get; private set; }
    // Whether THIS refresh token was issued from a "Remember me" login — carried forward across
    // every rotation (see LocalAuthService.RefreshTokenAsync, which reads this back in rather than
    // defaulting to false) so a remembered session doesn't silently downgrade to a session-only
    // cookie the first time its access token refreshes. Defaults to true so existing rows (issued
    // before this column existed, when every refresh cookie was unconditionally persistent) keep
    // behaving exactly as they do today after the migration backfills it — see the migration's own
    // defaultValue for the DB-side half of that guarantee.
    public bool RefreshTokenRememberMe { get; private set; } = true;

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

    /// <summary>Fully disables MFA and clears the secret, backup codes, and any pending login challenge.</summary>
    public void DisableMfa()
    {
        MfaEnabled = false;
        MfaSecret = null;
        MfaBackupCodeHashes = null;
        MfaEnrolledOnUtc = null;
        MfaChallengeTokenHash = null;
        MfaChallengeExpiresOnUtc = null;
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

    /// <summary>Stores the hash of a newly issued login-time MFA challenge token.</summary>
    public void SetMfaChallengeToken(string tokenHash, DateTime expiresOnUtc)
    {
        MfaChallengeTokenHash = tokenHash;
        MfaChallengeExpiresOnUtc = expiresOnUtc;
    }

    /// <summary>Clears the login-time MFA challenge token once it has been consumed or should no longer be usable.</summary>
    public void ClearMfaChallengeToken()
    {
        MfaChallengeTokenHash = null;
        MfaChallengeExpiresOnUtc = null;
    }

    /// <summary>Sets or clears the admin policy requiring this account to have MFA enabled.</summary>
    public void SetMustSetupMfa(bool required)
    {
        MustSetupMfa = required;
    }

    /// <summary>HIPAA #12: rotation period applied whenever a password is (re)set. See policies/09-access-control-and-authentication.md.</summary>
    public static readonly TimeSpan PasswordRotationPeriod = TimeSpan.FromDays(90);

    public void EnableLocalLogin(string passwordHash, bool mustChangePassword)
    {
        PasswordHash = passwordHash;
        IsLocalLoginEnabled = true;
        MustChangePassword = mustChangePassword;
        LastPasswordChangedOnUtc = DateTime.UtcNow;
        PasswordExpiresOnUtc = DateTime.UtcNow.Add(PasswordRotationPeriod);
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
        PasswordExpiresOnUtc = DateTime.UtcNow.Add(PasswordRotationPeriod);
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

    /// <summary>Stores the hash of a newly requested magic-link sign-in token.</summary>
    public void SetMagicLinkToken(string tokenHash, DateTime expiresOnUtc)
    {
        MagicLinkTokenHash = tokenHash;
        MagicLinkTokenExpiresOnUtc = expiresOnUtc;
    }

    /// <summary>Clears the magic-link token once it has been consumed or should no longer be usable.</summary>
    public void ClearMagicLinkToken()
    {
        MagicLinkTokenHash = null;
        MagicLinkTokenExpiresOnUtc = null;
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
        PasswordExpiresOnUtc = DateTime.UtcNow.Add(PasswordRotationPeriod);
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

    /// <summary>Stores a hashed refresh token for the 30-day refresh flow. `rememberMe` records whether
    /// this specific token should back a persistent (survives browser close) or session-only refresh
    /// cookie — see AuthController.CookieOptionsFor's `isPersistent` parameter, which reads this value
    /// back via the login response.</summary>
    public void SetRefreshToken(string refreshTokenHash, DateTime expiresOnUtc, bool rememberMe)
    {
        RefreshTokenHash = refreshTokenHash;
        RefreshTokenExpiresOnUtc = expiresOnUtc;
        RefreshTokenRememberMe = rememberMe;
    }

    /// <summary>Clears the refresh token, effectively logging the user out of the refresh flow.</summary>
    public void ClearRefreshToken()
    {
        RefreshTokenHash = null;
        RefreshTokenExpiresOnUtc = null;
        RefreshTokenRememberMe = true;
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

using FHIRBridge.Application.Abstractions.Caching;
using FHIRBridge.Application.Abstractions.Notifications;
using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Application.Security;
using FHIRBridge.Application.Services;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Governance;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Moq;

namespace FHIRBridge.UnitTests.Auth;

/// <summary>
/// Covers LocalAuthService.ForgotPasswordAsync/ResetPasswordAsync directly — no unit tests existed for
/// either method before this file. The HTTP-contract-level behavior (202/204/401/400 status codes,
/// [EnableRateLimiting("auth")]) lives in AuthController and is covered by the existing integration tests
/// in AuthTests.cs, not duplicated here.
/// </summary>
public sealed class ForgotPasswordResetPasswordTests
{
    private readonly Mock<IUserAccessRepository> _repository = new();
    private readonly Mock<IPasswordHasher> _passwordHasher = new();
    private readonly Mock<IAccessTokenIssuer> _accessTokenIssuer = new();
    private readonly Mock<ICurrentUserService> _currentUser = new();
    private readonly Mock<IEmailSender> _email = new();
    private readonly Mock<ITotpService> _totp = new();
    private readonly Mock<IGovernanceLogger> _governanceLogger = new();
    private readonly Mock<ISystemSettingsCache> _settingsCache = PassThroughSettingsCache();
    private readonly Mock<ITenantRepository> _tenantRepository = new();
    private readonly LocalAuthOptions _options = new();

    private static Mock<ISystemSettingsCache> PassThroughSettingsCache()
    {
        var mock = new Mock<ISystemSettingsCache>();
        mock.Setup(x => x.GetIntAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, int defaultValue, CancellationToken _) => defaultValue);
        mock.Setup(x => x.GetBoolAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, bool defaultValue, CancellationToken _) => defaultValue);
        mock.Setup(x => x.GetStringAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, string defaultValue, CancellationToken _) => defaultValue);
        return mock;
    }

    public ForgotPasswordResetPasswordTests()
    {
        // Safe default — no test here cares about tenant display name specifically.
        _tenantRepository.Setup(x => x.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Tenant?)null);

        // Both real ICurrentUserService implementations (HttpContextCurrentUserService,
        // SystemCurrentUserService) always return a non-null CurrentUserInfo, so an unconfigured mock
        // returning null is a test artifact rather than a state production can reach. It matters here
        // because ForgotPasswordAsync builds the reset link from CurrentUser.RequestOrigin: a null would
        // throw inside the try/catch that guards email delivery, silently skipping the send the tests
        // assert on. RequestOrigin is left null — no test here exercises a trusted-origin link, and that
        // is the value the Worker's own non-HTTP implementation reports.
        _currentUser.Setup(x => x.CurrentUser).Returns(
            new CurrentUserInfo(null, Email, null, Roles: [], IsAuthenticated: false));
    }

    private LocalAuthService Service() => new(
        _repository.Object,
        _passwordHasher.Object,
        _accessTokenIssuer.Object,
        _currentUser.Object,
        _email.Object,
        _totp.Object,
        _governanceLogger.Object,
        _settingsCache.Object,
        Options.Create(_options),
        _tenantRepository.Object);

    private const string Email = "reset-me@x.io";
    private const string OldPasswordHash = "old-hashed-password";

    private static User LocalLoginUser()
    {
        var user = new User("local:reset-me", Email, "Reset Me User", Guid.NewGuid());
        user.EnableLocalLogin(OldPasswordHash, mustChangePassword: false);
        return user;
    }

    // A real (non-mocked) hasher for the tests that need to verify actual hash/verify round-tripping
    // (i.e. that what's stored is genuinely a hash of the token, not the token itself).
    private static readonly FHIRBridge.Infrastructure.Security.Pbkdf2PasswordHasher RealHasher = new();

    // ── ForgotPasswordAsync ──────────────────────────────────────────────────────

    [Fact]
    public async Task ForgotPasswordAsync_existing_local_user_returns_Accepted_and_persists_a_hashed_token()
    {
        var user = LocalLoginUser();
        _repository.Setup(x => x.GetUserByEmailAsync(Email, It.IsAny<CancellationToken>())).ReturnsAsync(user);
        _passwordHasher.Setup(x => x.Hash(It.IsAny<string>())).Returns<string>(v => $"hashed:{v}");
        _email.Setup(x => x.SendAsync(Email, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var response = await Service().ForgotPasswordAsync(new ForgotPasswordRequest(Email), CancellationToken.None);

        response.Accepted.Should().BeTrue();
        response.ResetToken.Should().NotBeNullOrEmpty();
        response.ExpiresOnUtc.Should().NotBeNull();
        response.ExpiresOnUtc!.Value.Should().BeCloseTo(DateTime.UtcNow.AddMinutes(30), TimeSpan.FromSeconds(5));

        // Requirement: only the HASH is ever stored, never the raw token.
        user.PasswordResetTokenHash.Should().NotBeNullOrEmpty();
        user.PasswordResetTokenHash.Should().NotBe(response.ResetToken);
        user.PasswordResetTokenExpiresOnUtc.Should().Be(response.ExpiresOnUtc);

        _repository.Verify(x => x.UpdateUserAsync(user, It.IsAny<CancellationToken>()), Times.Once);
        _email.Verify(x => x.SendAsync(Email, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ForgotPasswordAsync_stores_a_genuine_hash_not_the_raw_token()
    {
        // Uses the REAL Pbkdf2PasswordHasher (not a mock) so this proves the stored value is actually a
        // hash that verifies against the returned token — not merely "a different string".
        var user = LocalLoginUser();
        _repository.Setup(x => x.GetUserByEmailAsync(Email, It.IsAny<CancellationToken>())).ReturnsAsync(user);
        _passwordHasher.Setup(x => x.Hash(It.IsAny<string>())).Returns<string>(RealHasher.Hash);
        _passwordHasher.Setup(x => x.Verify(It.IsAny<string>(), It.IsAny<string>())).Returns<string, string>(RealHasher.Verify);
        _email.Setup(x => x.SendAsync(Email, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var response = await Service().ForgotPasswordAsync(new ForgotPasswordRequest(Email), CancellationToken.None);

        user.PasswordResetTokenHash.Should().NotBe(response.ResetToken);
        RealHasher.Verify(response.ResetToken!, user.PasswordResetTokenHash!).Should().BeTrue();
        RealHasher.Verify("some-other-guess", user.PasswordResetTokenHash!).Should().BeFalse();
    }

    [Fact]
    public async Task ForgotPasswordAsync_nonexistent_email_returns_the_same_generic_Accepted_response()
    {
        _repository.Setup(x => x.GetUserByEmailAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((User?)null);

        var response = await Service().ForgotPasswordAsync(
            new ForgotPasswordRequest("nobody@nowhere.test"), CancellationToken.None);

        response.Accepted.Should().BeTrue();
        response.ResetToken.Should().BeNull();
        response.ExpiresOnUtc.Should().BeNull();
        _email.Verify(x => x.SendAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _repository.Verify(x => x.UpdateUserAsync(It.IsAny<User>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ForgotPasswordAsync_email_delivery_failure_still_returns_Accepted_and_does_not_throw()
    {
        // Requirement: an email infrastructure failure must not change the public response shape (that
        // would itself be an enumeration oracle, since only real accounts ever reach the send call) and
        // must not surface as an unhandled exception / 500.
        var user = LocalLoginUser();
        _repository.Setup(x => x.GetUserByEmailAsync(Email, It.IsAny<CancellationToken>())).ReturnsAsync(user);
        _passwordHasher.Setup(x => x.Hash(It.IsAny<string>())).Returns<string>(v => $"hashed:{v}");
        _email.Setup(x => x.SendAsync(Email, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("SMTP host unreachable"));

        var act = async () => await Service().ForgotPasswordAsync(new ForgotPasswordRequest(Email), CancellationToken.None);

        var response = await act.Should().NotThrowAsync();
        response.Subject.Accepted.Should().BeTrue();
        response.Subject.ResetToken.Should().NotBeNullOrEmpty();

        // The failure must still be surfaced internally (not silently dropped).
        _governanceLogger.Verify(x => x.LogSecurityEventAsync(
            It.Is<SecurityEventEntry>(e => e.EventType == "PasswordResetEmailDeliveryFailed" && e.UserEmail == Email),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── ResetPasswordAsync ───────────────────────────────────────────────────────

    private static (User User, string RawToken) UserWithValidResetToken(DateTime? expiresOnUtc = null)
    {
        var user = LocalLoginUser();
        const string rawToken = "the-raw-token-from-the-email-link";
        user.SetPasswordResetToken(RealHasher.Hash(rawToken), expiresOnUtc ?? DateTime.UtcNow.AddMinutes(30));
        return (user, rawToken);
    }

    private LocalAuthService ServiceWithRealHasher()
    {
        _passwordHasher.Setup(x => x.Hash(It.IsAny<string>())).Returns<string>(RealHasher.Hash);
        _passwordHasher.Setup(x => x.Verify(It.IsAny<string>(), It.IsAny<string>())).Returns<string, string>(RealHasher.Verify);
        return Service();
    }

    [Fact]
    public async Task ResetPasswordAsync_valid_token_updates_password_clears_reset_token_and_invalidates_refresh_token()
    {
        var (user, rawToken) = UserWithValidResetToken();
        user.SetRefreshToken("some-old-refresh-hash", DateTime.UtcNow.AddDays(30), rememberMe: true);
        _repository.Setup(x => x.GetUserByEmailAsync(Email, It.IsAny<CancellationToken>())).ReturnsAsync(user);

        await ServiceWithRealHasher().ResetPasswordAsync(
            new ResetPasswordRequest(Email, rawToken, "Br4nd!NewPassw0rd"), CancellationToken.None);

        RealHasher.Verify("Br4nd!NewPassw0rd", user.PasswordHash!).Should().BeTrue();
        user.PasswordResetTokenHash.Should().BeNull();
        user.PasswordResetTokenExpiresOnUtc.Should().BeNull();

        // Critical security requirement: any refresh token issued before the reset must stop working.
        user.RefreshTokenHash.Should().BeNull();
        user.RefreshTokenExpiresOnUtc.Should().BeNull();

        _repository.Verify(x => x.UpdateUserAsync(user, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ResetPasswordAsync_invalid_token_is_rejected_and_does_not_touch_the_password()
    {
        var (user, _) = UserWithValidResetToken();
        _repository.Setup(x => x.GetUserByEmailAsync(Email, It.IsAny<CancellationToken>())).ReturnsAsync(user);

        var act = async () => await ServiceWithRealHasher().ResetPasswordAsync(
            new ResetPasswordRequest(Email, "totally-wrong-token", "Br4nd!NewPassw0rd"), CancellationToken.None);

        (await act.Should().ThrowAsync<InvalidOperationException>()).WithMessage("Reset token is invalid or expired.");
        user.PasswordHash.Should().Be(OldPasswordHash);
        user.PasswordResetTokenHash.Should().NotBeNullOrEmpty(); // untouched — a failed attempt doesn't burn the token
    }

    [Fact]
    public async Task ResetPasswordAsync_expired_token_is_rejected_even_with_the_correct_raw_value()
    {
        var (user, rawToken) = UserWithValidResetToken(expiresOnUtc: DateTime.UtcNow.AddMinutes(-1));
        _repository.Setup(x => x.GetUserByEmailAsync(Email, It.IsAny<CancellationToken>())).ReturnsAsync(user);

        var act = async () => await ServiceWithRealHasher().ResetPasswordAsync(
            new ResetPasswordRequest(Email, rawToken, "Br4nd!NewPassw0rd"), CancellationToken.None);

        (await act.Should().ThrowAsync<InvalidOperationException>()).WithMessage("Reset token is invalid or expired.");
    }

    [Fact]
    public async Task ResetPasswordAsync_unknown_email_is_rejected_with_the_same_generic_message()
    {
        _repository.Setup(x => x.GetUserByEmailAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((User?)null);

        var act = async () => await ServiceWithRealHasher().ResetPasswordAsync(
            new ResetPasswordRequest("nobody@nowhere.test", "any-token", "Br4nd!NewPassw0rd"), CancellationToken.None);

        (await act.Should().ThrowAsync<InvalidOperationException>()).WithMessage("Reset token is invalid or expired.");
    }

    [Fact]
    public async Task ResetPasswordAsync_a_used_token_cannot_be_replayed()
    {
        var (user, rawToken) = UserWithValidResetToken();
        _repository.Setup(x => x.GetUserByEmailAsync(Email, It.IsAny<CancellationToken>())).ReturnsAsync(user);
        var service = ServiceWithRealHasher();

        // First use succeeds.
        await service.ResetPasswordAsync(new ResetPasswordRequest(Email, rawToken, "Br4nd!NewPassw0rd"), CancellationToken.None);

        // Same raw token, same email, second attempt — must fail now that the hash has been cleared.
        var act = async () => await service.ResetPasswordAsync(
            new ResetPasswordRequest(Email, rawToken, "Another!NewPassw0rd2"), CancellationToken.None);

        (await act.Should().ThrowAsync<InvalidOperationException>()).WithMessage("Reset token is invalid or expired.");
    }

    [Theory]
    [InlineData("Short1!")]                    // too short
    [InlineData("nouppercasehere123!")]        // missing uppercase
    [InlineData("NOLOWERCASEHERE123!")]        // missing lowercase
    [InlineData("NoDigitsHereEither!")]        // missing digit
    [InlineData("NoSpecialCharHere123")]       // missing special character
    public async Task ResetPasswordAsync_rejects_passwords_that_violate_the_policy_before_checking_the_token(string weakPassword)
    {
        var (user, rawToken) = UserWithValidResetToken();
        _repository.Setup(x => x.GetUserByEmailAsync(Email, It.IsAny<CancellationToken>())).ReturnsAsync(user);

        var act = async () => await ServiceWithRealHasher().ResetPasswordAsync(
            new ResetPasswordRequest(Email, rawToken, weakPassword), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        // A rejected password must not consume the token — the user can retry with a stronger one.
        user.PasswordResetTokenHash.Should().NotBeNullOrEmpty();
        user.PasswordHash.Should().Be(OldPasswordHash);
    }

    [Fact]
    public async Task ResetPasswordAsync_does_not_issue_a_session___no_auto_login_after_reset()
    {
        var (user, rawToken) = UserWithValidResetToken();
        _repository.Setup(x => x.GetUserByEmailAsync(Email, It.IsAny<CancellationToken>())).ReturnsAsync(user);

        // ResetPasswordAsync returns Task (void), not Task<LocalLoginResponse> — there is no response to
        // carry a session/cookie in, which is itself the proof no session is issued. This test documents
        // that contract explicitly rather than relying on the method signature alone.
        await ServiceWithRealHasher().ResetPasswordAsync(
            new ResetPasswordRequest(Email, rawToken, "Br4nd!NewPassw0rd"), CancellationToken.None);

        _accessTokenIssuer.Verify(x => x.Issue(It.IsAny<User>(), It.IsAny<IReadOnlyCollection<string>>()), Times.Never);
        _accessTokenIssuer.Verify(x => x.IssueRefreshToken(), Times.Never);
    }
}

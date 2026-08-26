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
/// Covers the actual bug this feature fixes: LocalAuthService.LoginAsync/CompleteMfaLoginAsync must
/// carry the caller's "Remember me" choice into LocalLoginResponse.RememberMe (which
/// AuthController.IssueTokenCookiesAndStrip reads to decide refresh/CSRF cookie persistence — not
/// tested here, that's the integration-level half), and RefreshTokenAsync/ChangePasswordAsync must
/// preserve the ORIGINAL choice across rotation rather than resetting it, since neither of those
/// requests carries a fresh rememberMe value at all.
/// </summary>
public sealed class RememberMeTests
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

    public RememberMeTests()
    {
        // None of these tests care about tenant display name specifically — null is a safe, valid
        // "tenant row not found" response that CreateLoginResponseAsync already handles gracefully
        // (falls back to an empty TenantName). Explicit rather than relying on Moq's default Task
        // handling, since every login-family call now goes through this lookup.
        _tenantRepository.Setup(x => x.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Tenant?)null);
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

    private const string Email = "remember-me-user@x.io";
    private const string Password = "raw-password";
    private const string PasswordHash = "hashed-password";

    private static User LocalLoginUser()
    {
        var user = new User("local:remember-me-user", Email, "Remember Me User", Guid.NewGuid());
        user.EnableLocalLogin(PasswordHash, mustChangePassword: false);
        return user;
    }

    /// <summary>Every CreateLoginResponseAsync call needs these — factored out since the exact
    /// values don't matter to any assertion here, only that the call succeeds.</summary>
    private void SetUpSuccessfulTokenIssuance(User user)
    {
        _repository.Setup(x => x.GetUserRolesAsync(user.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<Role>)[]);
        _repository.Setup(x => x.GetUserPermissionAllocationsAsync(user.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<(Permission Permission, bool IsEnabled)>)[]);
        _accessTokenIssuer.Setup(x => x.Issue(user, It.IsAny<IReadOnlyCollection<string>>()))
            .Returns(new AccessTokenDto("access-token", "Bearer", DateTime.UtcNow.AddHours(1)));
        _accessTokenIssuer.Setup(x => x.IssueRefreshToken())
            .Returns(("refresh-hash", DateTime.UtcNow.AddDays(30)));
    }

    [Fact]
    public async Task LoginAsync_with_RememberMe_true_returns_RememberMe_true()
    {
        var user = LocalLoginUser();
        _repository.Setup(x => x.GetUserByEmailAsync(Email, It.IsAny<CancellationToken>())).ReturnsAsync(user);
        _passwordHasher.Setup(x => x.Verify(Password, PasswordHash)).Returns(true);
        SetUpSuccessfulTokenIssuance(user);

        var response = await Service().LoginAsync(new LocalLoginRequest(Email, Password, RememberMe: true), CancellationToken.None);

        response.RequiresMfa.Should().BeFalse();
        response.RememberMe.Should().BeTrue();
        user.RefreshTokenRememberMe.Should().BeTrue();
    }

    [Fact]
    public async Task LoginAsync_with_RememberMe_false_returns_RememberMe_false()
    {
        var user = LocalLoginUser();
        _repository.Setup(x => x.GetUserByEmailAsync(Email, It.IsAny<CancellationToken>())).ReturnsAsync(user);
        _passwordHasher.Setup(x => x.Verify(Password, PasswordHash)).Returns(true);
        SetUpSuccessfulTokenIssuance(user);

        var response = await Service().LoginAsync(new LocalLoginRequest(Email, Password, RememberMe: false), CancellationToken.None);

        response.RequiresMfa.Should().BeFalse();
        response.RememberMe.Should().BeFalse();
        user.RefreshTokenRememberMe.Should().BeFalse();
    }

    [Fact]
    public async Task LoginAsync_RememberMe_defaults_to_false_when_omitted()
    {
        // LocalLoginRequest(Email, Password) — no third argument — must behave exactly like an
        // explicit false, not silently fall back to some other default.
        var user = LocalLoginUser();
        _repository.Setup(x => x.GetUserByEmailAsync(Email, It.IsAny<CancellationToken>())).ReturnsAsync(user);
        _passwordHasher.Setup(x => x.Verify(Password, PasswordHash)).Returns(true);
        SetUpSuccessfulTokenIssuance(user);

        var response = await Service().LoginAsync(new LocalLoginRequest(Email, Password), CancellationToken.None);

        response.RememberMe.Should().BeFalse();
    }

    [Fact]
    public async Task CompleteMfaLoginAsync_forwards_the_RememberMe_choice_from_its_own_request()
    {
        var user = LocalLoginUser();
        user.BeginMfaEnrollment("SECRET");
        user.ConfirmMfaEnrollment(["backup-hash-1"]);
        const string challengeToken = "mfa-challenge-token";
        const string totpCode = "123456";
        user.SetMfaChallengeToken(challengeToken, DateTime.UtcNow.AddMinutes(5));
        _repository.Setup(x => x.GetUserByMfaChallengeTokenHashAsync(challengeToken, It.IsAny<CancellationToken>()))
            .ReturnsAsync(user);
        _totp.Setup(x => x.ValidateCode(user.MfaSecret!, totpCode)).Returns(true);
        SetUpSuccessfulTokenIssuance(user);

        var response = await Service().CompleteMfaLoginAsync(
            new MfaLoginRequest(challengeToken, totpCode, RememberMe: true), CancellationToken.None);

        response.RequiresMfa.Should().BeFalse();
        response.RememberMe.Should().BeTrue();
        user.RefreshTokenRememberMe.Should().BeTrue();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RefreshTokenAsync_preserves_the_original_RememberMe_choice_across_rotation(bool originalRememberMe)
    {
        // The critical case this whole feature depends on: the /refresh request itself carries no
        // rememberMe at all — only the raw refresh token. If this silently defaulted to false on
        // every rotation, a remembered session would downgrade to session-only the very first time
        // its access token refreshed (every 60 minutes), defeating "survives a browser restart"
        // entirely. Server-side state (RefreshTokenRememberMe) is what has to carry it forward.
        var user = LocalLoginUser();
        var originalHash = "original-refresh-hash";
        user.SetRefreshToken(originalHash, DateTime.UtcNow.AddDays(30), originalRememberMe);
        _repository.Setup(x => x.GetUserByRefreshTokenHashAsync(originalHash, It.IsAny<CancellationToken>()))
            .ReturnsAsync(user);
        SetUpSuccessfulTokenIssuance(user);

        var response = await Service().RefreshTokenAsync(new RefreshTokenRequest(originalHash), CancellationToken.None);

        response.RememberMe.Should().Be(originalRememberMe);
        user.RefreshTokenRememberMe.Should().Be(originalRememberMe);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ChangePasswordAsync_preserves_the_current_sessions_RememberMe_choice(bool currentRememberMe)
    {
        var user = LocalLoginUser();
        user.SetRefreshToken("existing-hash", DateTime.UtcNow.AddDays(30), currentRememberMe);
        _currentUser.Setup(x => x.CurrentUser).Returns(
            new CurrentUserInfo(user.ExternalUserId, user.Email, user.DisplayName, Roles: [], IsAuthenticated: true));
        _repository.Setup(x => x.GetUserByExternalIdAsync(user.ExternalUserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(user);
        _passwordHasher.Setup(x => x.Verify("current-pw", PasswordHash)).Returns(true);
        _passwordHasher.Setup(x => x.Hash(It.IsAny<string>())).Returns("new-hash");
        SetUpSuccessfulTokenIssuance(user);

        var response = await Service().ChangePasswordAsync(
            new ChangePasswordRequest("current-pw", "New-Password-123!"), CancellationToken.None);

        response.RememberMe.Should().Be(currentRememberMe);
    }

    [Fact]
    public void ClearRefreshToken_resets_RememberMe_back_to_the_column_default()
    {
        var user = LocalLoginUser();
        user.SetRefreshToken("some-hash", DateTime.UtcNow.AddDays(30), rememberMe: false);

        user.ClearRefreshToken();

        user.RefreshTokenHash.Should().BeNull();
        user.RefreshTokenRememberMe.Should().BeTrue();
    }
}

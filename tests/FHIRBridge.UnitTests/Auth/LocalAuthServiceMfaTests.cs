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

public sealed class LocalAuthServiceMfaTests
{
    private readonly Mock<IUserAccessRepository> _repository = new();
    private readonly Mock<IPasswordHasher> _passwordHasher = new();
    private readonly Mock<IAccessTokenIssuer> _accessTokenIssuer = new();
    private readonly Mock<ICurrentUserService> _currentUser = new();
    private readonly Mock<IEmailSender> _email = new();
    private readonly Mock<ITotpService> _totp = new();
    private readonly Mock<IGovernanceLogger> _governanceLogger = new();
    private readonly Mock<ISystemSettingsCache> _settingsCache = PassThroughSettingsCache();
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

    private LocalAuthService Service() => new(
        _repository.Object,
        _passwordHasher.Object,
        _accessTokenIssuer.Object,
        _currentUser.Object,
        _email.Object,
        _totp.Object,
        _governanceLogger.Object,
        _settingsCache.Object,
        Options.Create(_options));

    private const string Email = "mfa-user@x.io";
    private const string Password = "Correct-Password-123!";
    private const string PasswordHash = "hashed-password";

    private static User MfaEnabledUser()
    {
        var user = new User("local:mfa-user", Email, "MFA User");
        user.EnableLocalLogin(PasswordHash, mustChangePassword: false);
        user.BeginMfaEnrollment("SECRET");
        user.ConfirmMfaEnrollment(["backup-hash-1"]);
        return user;
    }

    private void SetupPasswordVerification(User user, bool result = true)
    {
        _repository.Setup(x => x.GetUserByEmailAsync(Email, It.IsAny<CancellationToken>())).ReturnsAsync(user);
        _passwordHasher.Setup(x => x.Verify(Password, PasswordHash)).Returns(result);
    }

    [Fact]
    public async Task LoginAsync_correct_password_with_mfa_enabled_returns_challenge_without_lockout_penalty()
    {
        var user = MfaEnabledUser();
        SetupPasswordVerification(user);
        // LoginAsync mints the MFA challenge token by reusing IssueRefreshToken() purely for its
        // random-bytes-to-hash mint (see LocalAuthService.LoginAsync's MFA branch).
        _accessTokenIssuer.Setup(x => x.IssueRefreshToken()).Returns(("challenge-hash", DateTime.UtcNow.AddDays(30)));

        var response = await Service().LoginAsync(new LocalLoginRequest(Email, Password), CancellationToken.None);

        response.RequiresMfa.Should().BeTrue();
        response.MfaChallengeToken.Should().Be("challenge-hash");
        response.AccessToken.Should().BeNull();
        user.FailedLoginCount.Should().Be(0);
        user.MfaChallengeTokenHash.Should().Be("challenge-hash");
        _accessTokenIssuer.Verify(x => x.Issue(It.IsAny<User>(), It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<IReadOnlyCollection<string>>()), Times.Never);
    }

    [Fact]
    public async Task CompleteMfaLoginAsync_valid_code_against_live_challenge_issues_tokens_and_consumes_challenge()
    {
        // The challenge token is used verbatim as the repository lookup key (see
        // GenerateMfaChallengeToken/GetUserByMfaChallengeTokenHashAsync — no re-hash on the inbound side),
        // so the mocked repository is keyed on the exact same literal the request supplies.
        const string challengeToken = "test-challenge-token";
        var user = MfaEnabledUser();
        user.SetMfaChallengeToken(challengeToken, DateTime.UtcNow.AddMinutes(5));
        _repository.Setup(x => x.GetUserByMfaChallengeTokenHashAsync(challengeToken, It.IsAny<CancellationToken>()))
            .ReturnsAsync(user);
        _totp.Setup(x => x.ValidateCode("SECRET", "123456")).Returns(true);
        _accessTokenIssuer.Setup(x => x.Issue(user, It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<IReadOnlyCollection<string>>()))
            .Returns(new AccessTokenDto("access-token", "Bearer", DateTime.UtcNow.AddHours(1)));
        _accessTokenIssuer.Setup(x => x.IssueRefreshToken()).Returns(("refresh-hash", DateTime.UtcNow.AddDays(30)));
        _repository.Setup(x => x.GetUserRolesAsync(user.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<Role>)[]);
        _repository.Setup(x => x.GetUserPermissionAllocationsAsync(user.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<(Permission Permission, bool IsEnabled)>)[]);

        var response = await Service().CompleteMfaLoginAsync(
            new MfaLoginRequest(challengeToken, "123456"), CancellationToken.None);

        response.RequiresMfa.Should().BeFalse();
        response.AccessToken.Should().Be("access-token");
        user.MfaChallengeTokenHash.Should().BeNull();
        user.FailedLoginCount.Should().Be(0);
    }

    [Fact]
    public async Task CompleteMfaLoginAsync_wrong_code_registers_failed_login_but_keeps_challenge_alive()
    {
        const string challengeToken = "test-challenge-token";
        var user = MfaEnabledUser();
        user.SetMfaChallengeToken(challengeToken, DateTime.UtcNow.AddMinutes(5));
        _repository.Setup(x => x.GetUserByMfaChallengeTokenHashAsync(challengeToken, It.IsAny<CancellationToken>()))
            .ReturnsAsync(user);
        _totp.Setup(x => x.ValidateCode("SECRET", "000000")).Returns(false);

        var act = () => Service().CompleteMfaLoginAsync(
            new MfaLoginRequest(challengeToken, "000000"), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        user.FailedLoginCount.Should().Be(1);
        user.MfaChallengeTokenHash.Should().Be(challengeToken);
    }

    [Fact]
    public async Task CompleteMfaLoginAsync_expired_challenge_throws_without_lockout_penalty()
    {
        const string challengeToken = "test-challenge-token";
        var user = MfaEnabledUser();
        user.SetMfaChallengeToken(challengeToken, DateTime.UtcNow.AddMinutes(-1));
        _repository.Setup(x => x.GetUserByMfaChallengeTokenHashAsync(challengeToken, It.IsAny<CancellationToken>()))
            .ReturnsAsync(user);

        var act = () => Service().CompleteMfaLoginAsync(
            new MfaLoginRequest(challengeToken, "123456"), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        user.FailedLoginCount.Should().Be(0);
        _totp.Verify(x => x.ValidateCode(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task CompleteMfaLoginAsync_unknown_challenge_token_throws()
    {
        _repository.Setup(x => x.GetUserByMfaChallengeTokenHashAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((User?)null);

        var act = () => Service().CompleteMfaLoginAsync(
            new MfaLoginRequest("unknown-token", "123456"), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }
}

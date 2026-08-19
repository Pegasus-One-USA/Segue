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

public sealed class MagicLinkAuthTests
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

    private const string Email = "magic-user@x.io";
    private const string Token = "raw-magic-link-token";
    private const string TokenHash = "hashed-magic-link-token";

    private static User LocalLoginUser()
    {
        var user = new User("local:magic-user", Email, "Magic Link User");
        user.EnableLocalLogin("password-hash", mustChangePassword: false);
        return user;
    }

    [Fact]
    public async Task RequestMagicLinkAsync_known_local_user_sets_token_and_sends_email()
    {
        var user = LocalLoginUser();
        _repository.Setup(x => x.GetUserByEmailAsync(Email, It.IsAny<CancellationToken>())).ReturnsAsync(user);
        _passwordHasher.Setup(x => x.Hash(It.IsAny<string>())).Returns(TokenHash);

        var response = await Service().RequestMagicLinkAsync(new MagicLinkRequest(Email), CancellationToken.None);

        response.Accepted.Should().BeTrue();
        user.MagicLinkTokenHash.Should().Be(TokenHash);
        user.MagicLinkTokenExpiresOnUtc.Should().NotBeNull();
        _email.Verify(x => x.SendAsync(Email, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RequestMagicLinkAsync_unknown_email_reports_accepted_without_sending_email()
    {
        _repository.Setup(x => x.GetUserByEmailAsync(Email, It.IsAny<CancellationToken>())).ReturnsAsync((User?)null);

        var response = await Service().RequestMagicLinkAsync(new MagicLinkRequest(Email), CancellationToken.None);

        // No user enumeration: reports the same "accepted" shape whether or not an account exists.
        response.Accepted.Should().BeTrue();
        _email.Verify(x => x.SendAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RedeemMagicLinkAsync_valid_token_clears_token_and_issues_session()
    {
        var user = LocalLoginUser();
        user.SetMagicLinkToken(TokenHash, DateTime.UtcNow.AddMinutes(15));
        _repository.Setup(x => x.GetUserByEmailAsync(Email, It.IsAny<CancellationToken>())).ReturnsAsync(user);
        _passwordHasher.Setup(x => x.Verify(Token, TokenHash)).Returns(true);
        _accessTokenIssuer.Setup(x => x.Issue(user, It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<IReadOnlyCollection<string>>()))
            .Returns(new AccessTokenDto("access-token", "Bearer", DateTime.UtcNow.AddHours(1)));
        _accessTokenIssuer.Setup(x => x.IssueRefreshToken()).Returns(("refresh-hash", DateTime.UtcNow.AddDays(30)));
        _repository.Setup(x => x.GetUserRolesAsync(user.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<Role>)[]);
        _repository.Setup(x => x.GetUserPermissionAllocationsAsync(user.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<(Permission Permission, bool IsEnabled)>)[]);

        var response = await Service().RedeemMagicLinkAsync(new MagicLinkRedeemRequest(Email, Token), CancellationToken.None);

        response.RequiresMfa.Should().BeFalse();
        response.AccessToken.Should().Be("access-token");
        user.MagicLinkTokenHash.Should().BeNull();
        user.MagicLinkTokenExpiresOnUtc.Should().BeNull();
    }

    [Fact]
    public async Task RedeemMagicLinkAsync_mfa_enabled_returns_challenge_without_issuing_session()
    {
        var user = LocalLoginUser();
        user.SetMagicLinkToken(TokenHash, DateTime.UtcNow.AddMinutes(15));
        user.BeginMfaEnrollment("SECRET");
        user.ConfirmMfaEnrollment(["backup-hash-1"]);
        _repository.Setup(x => x.GetUserByEmailAsync(Email, It.IsAny<CancellationToken>())).ReturnsAsync(user);
        _passwordHasher.Setup(x => x.Verify(Token, TokenHash)).Returns(true);
        _accessTokenIssuer.Setup(x => x.IssueRefreshToken()).Returns(("challenge-hash", DateTime.UtcNow.AddDays(30)));

        var response = await Service().RedeemMagicLinkAsync(new MagicLinkRedeemRequest(Email, Token), CancellationToken.None);

        response.RequiresMfa.Should().BeTrue();
        response.MfaChallengeToken.Should().Be("challenge-hash");
        user.MagicLinkTokenHash.Should().BeNull();
        _accessTokenIssuer.Verify(x => x.Issue(It.IsAny<User>(), It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<IReadOnlyCollection<string>>()), Times.Never);
    }

    [Fact]
    public async Task RedeemMagicLinkAsync_expired_token_throws_and_registers_failed_login()
    {
        var user = LocalLoginUser();
        user.SetMagicLinkToken(TokenHash, DateTime.UtcNow.AddMinutes(-1));
        _repository.Setup(x => x.GetUserByEmailAsync(Email, It.IsAny<CancellationToken>())).ReturnsAsync(user);

        var act = () => Service().RedeemMagicLinkAsync(new MagicLinkRedeemRequest(Email, Token), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        user.FailedLoginCount.Should().Be(1);
    }

    [Fact]
    public async Task RedeemMagicLinkAsync_wrong_token_throws()
    {
        var user = LocalLoginUser();
        user.SetMagicLinkToken(TokenHash, DateTime.UtcNow.AddMinutes(15));
        _repository.Setup(x => x.GetUserByEmailAsync(Email, It.IsAny<CancellationToken>())).ReturnsAsync(user);
        _passwordHasher.Setup(x => x.Verify(It.IsAny<string>(), TokenHash)).Returns(false);

        var act = () => Service().RedeemMagicLinkAsync(new MagicLinkRedeemRequest(Email, "wrong-token"), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        user.MagicLinkTokenHash.Should().Be(TokenHash);
    }

    [Fact]
    public async Task RedeemMagicLinkAsync_unknown_email_throws()
    {
        _repository.Setup(x => x.GetUserByEmailAsync(Email, It.IsAny<CancellationToken>())).ReturnsAsync((User?)null);

        var act = () => Service().RedeemMagicLinkAsync(new MagicLinkRedeemRequest(Email, Token), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }
}

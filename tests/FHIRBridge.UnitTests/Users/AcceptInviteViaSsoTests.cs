using FHIRBridge.Application.Abstractions.Notifications;
using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Application.Security;
using FHIRBridge.Application.Services;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Domain.Enums;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace FHIRBridge.UnitTests.Users;

public sealed class AcceptInviteViaSsoTests
{
    private readonly Mock<IUserAccessRepository> _repository = new();
    private readonly Mock<IPasswordHasher> _passwordHasher = new();
    private readonly Mock<ICurrentUserService> _currentUser = new();
    private readonly Mock<IEmailSender> _email = new();
    private readonly Mock<IExternalTokenValidator> _externalTokenValidator = new();
    private readonly Mock<ILocalAuthService> _localAuth = new();

    public AcceptInviteViaSsoTests()
    {
        _currentUser.SetupGet(x => x.CurrentUser)
            .Returns(new CurrentUserInfo(null, null, null, [], IsAuthenticated: false));
    }

    private UserManagementService Service() => new(
        _repository.Object,
        _passwordHasher.Object,
        _currentUser.Object,
        new FHIRBridge.UnitTests.Security.PassthroughUserDisplayNameResolver(),
        _email.Object,
        _externalTokenValidator.Object,
        _localAuth.Object,
        Options.Create(new LocalAuthOptions()),
        NullLogger<UserManagementService>.Instance);

    private static User InvitedUser(string email)
    {
        var user = new User($"local:{email}", email, null, Guid.NewGuid());
        user.SetInvited("hashed-token", DateTime.UtcNow.AddHours(1));
        return user;
    }

    [Fact]
    public async Task AcceptInviteViaSso_throws_400_when_external_email_differs_from_invite()
    {
        const string inviteEmail = "invited@x.io";
        _repository.Setup(x => x.GetUserByEmailAsync(inviteEmail, It.IsAny<CancellationToken>()))
            .ReturnsAsync(InvitedUser(inviteEmail));
        _passwordHasher.Setup(x => x.Verify("raw-token", "hashed-token")).Returns(true);
        _externalTokenValidator
            .Setup(x => x.ValidateAsync(LoginProvider.Google, "ext-token", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExternalIdentity(LoginProvider.Google, "sub-1", "someone-else@x.io", "Other"));

        var act = () => Service().AcceptInviteViaSsoAsync(
            new AcceptInviteSsoRequest(inviteEmail, "raw-token", LoginProvider.Google, "ext-token", AcceptTerms: true),
            CancellationToken.None);

        // ArgumentException -> HTTP 400 via the API exception mapper.
        await act.Should().ThrowAsync<ArgumentException>();
        // Expression trees can't contain a call relying on an omitted optional argument (CS0854) —
        // rememberMe must be passed explicitly here now that IssueSessionAsync has one; It.IsAny<bool>()
        // keeps this assertion about call count, not about the specific value, same as before.
        _localAuth.Verify(x => x.IssueSessionAsync(It.IsAny<User>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public async Task AcceptInviteViaSso_links_identity_and_issues_session_when_email_matches()
    {
        const string inviteEmail = "invited@x.io";
        var user = InvitedUser(inviteEmail);
        _repository.Setup(x => x.GetUserByEmailAsync(inviteEmail, It.IsAny<CancellationToken>())).ReturnsAsync(user);
        _passwordHasher.Setup(x => x.Verify("raw-token", "hashed-token")).Returns(true);
        _externalTokenValidator
            .Setup(x => x.ValidateAsync(LoginProvider.Google, "ext-token", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExternalIdentity(LoginProvider.Google, "sub-1", "INVITED@x.io", "Invited"));

        await Service().AcceptInviteViaSsoAsync(
            new AcceptInviteSsoRequest(inviteEmail, "raw-token", LoginProvider.Google, "ext-token", AcceptTerms: true),
            CancellationToken.None);

        user.Status.Should().Be(UserStatus.Active);
        user.LoginProvider.Should().Be(LoginProvider.Google);
        user.ExternalUserId.Should().Be("sub-1");
        user.IsLocalLoginEnabled.Should().BeFalse();
        // Same CS0854 reasoning as the test above — It.IsAny<bool>() rather than the omitted default.
        _localAuth.Verify(x => x.IssueSessionAsync(user, It.IsAny<CancellationToken>(), It.IsAny<bool>()), Times.Once);
    }
}

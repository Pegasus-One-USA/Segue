using FHIRBridge.Application.Abstractions.Notifications;
using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Application.Services;
using FHIRBridge.Domain.Entities;
using FluentAssertions;
using Moq;

namespace FHIRBridge.UnitTests.Setup;

public sealed class SetupServiceTests
{
    private readonly Mock<IUserAccessRepository> _repository = new();
    private readonly Mock<IUserManagementService> _userManagement = new();
    private readonly Mock<ILocalAuthService> _localAuth = new();
    private readonly Mock<IExternalTokenValidator> _externalTokenValidator = new();
    private readonly Mock<INotificationSettingsService> _notificationSettings = new();

    private static readonly FirstRunEmailSettingsRequest EmailSettings =
        new("smtp.example.com", 587, EnableSsl: true, Username: null, Password: null, "no-reply@example.com", "FHIRBridge");

    private SetupService Service() =>
        new(_repository.Object, _userManagement.Object, _localAuth.Object, _externalTokenValidator.Object, _notificationSettings.Object);

    [Fact]
    public async Task RequiresSetup_is_true_when_no_users_exist()
    {
        _repository.Setup(x => x.GetUsersAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);

        (await Service().RequiresSetupAsync(CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task RequiresSetup_is_false_when_a_user_exists()
    {
        _repository.Setup(x => x.GetUsersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([new User("local:admin@x.io", "admin@x.io", "Admin", Guid.NewGuid())]);

        (await Service().RequiresSetupAsync(CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task CreateFirstSuperAdmin_throws_and_creates_nothing_once_initialized()
    {
        _repository.Setup(x => x.GetUsersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([new User("local:admin@x.io", "admin@x.io", "Admin", Guid.NewGuid())]);

        var act = () => Service().CreateFirstSuperAdminAsync(
            new CreateFirstSuperAdminRequest("new@x.io", "New", "SuperAdmin@Pass123!", AcceptTerms: true, EmailSettings),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        _userManagement.Verify(
            x => x.CreateLocalUserAsync(It.IsAny<CreateLocalUserRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CreateFirstSuperAdmin_throws_and_creates_nothing_when_terms_not_accepted()
    {
        _repository.Setup(x => x.GetUsersAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);

        var act = () => Service().CreateFirstSuperAdminAsync(
            new CreateFirstSuperAdminRequest("new@x.io", "New", "SuperAdmin@Pass123!", AcceptTerms: false, EmailSettings),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        _userManagement.Verify(
            x => x.CreateLocalUserAsync(It.IsAny<CreateLocalUserRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        _notificationSettings.Verify(
            x => x.UpdateAsync(It.IsAny<UpdateNotificationSettingsRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CreateFirstSuperAdmin_saves_email_settings_enabled_and_creates_the_admin()
    {
        _repository.Setup(x => x.GetUsersAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        _localAuth.Setup(x => x.LoginAsync(It.IsAny<LocalLoginRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LocalLoginResponse(
                RequiresMfa: false,
                MfaChallengeToken: null,
                MfaChallengeExpiresOnUtc: null,
                AccessToken: "token",
                TokenType: "Bearer",
                ExpiresOnUtc: DateTime.UtcNow,
                RequiresPasswordChange: false,
                Profile: null));

        await Service().CreateFirstSuperAdminAsync(
            new CreateFirstSuperAdminRequest("new@x.io", "New", "SuperAdmin@Pass123!", AcceptTerms: true, EmailSettings),
            CancellationToken.None);

        _notificationSettings.Verify(
            x => x.UpdateAsync(
                It.Is<UpdateNotificationSettingsRequest>(r => r.IsEnabled && r.Host == EmailSettings.Host),
                It.IsAny<CancellationToken>()),
            Times.Once);
        _userManagement.Verify(
            x => x.CreateLocalUserAsync(It.IsAny<CreateLocalUserRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }
}

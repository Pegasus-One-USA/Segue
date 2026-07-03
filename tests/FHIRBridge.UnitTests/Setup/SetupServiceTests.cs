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

    private SetupService Service() =>
        new(_repository.Object, _userManagement.Object, _localAuth.Object, _externalTokenValidator.Object);

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
            .ReturnsAsync([new User("local:admin@x.io", "admin@x.io", "Admin")]);

        (await Service().RequiresSetupAsync(CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task CreateFirstSuperAdmin_throws_and_creates_nothing_once_initialized()
    {
        _repository.Setup(x => x.GetUsersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([new User("local:admin@x.io", "admin@x.io", "Admin")]);

        var act = () => Service().CreateFirstSuperAdminAsync(
            new CreateFirstSuperAdminRequest("new@x.io", "New", "SuperAdmin@Pass123!"), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        _userManagement.Verify(
            x => x.CreateLocalUserAsync(It.IsAny<CreateLocalUserRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}

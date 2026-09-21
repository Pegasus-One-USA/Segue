using FHIRBridge.Application.Abstractions.Notifications;
using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.Security;
using FHIRBridge.Application.Services;
using FHIRBridge.Domain.Entities;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace FHIRBridge.UnitTests.Users;

/// <summary>
/// RBAC Fix 2 (Step 7 audit, confirmed medium-severity defect): UserManagementService.GetUserRolesAsync and
/// the private ToDetailDtoAsync (used by GetUserByIdAsync, i.e. GET /users/{id}) each construct a RoleDto
/// without passing role.IsFullAccess, so both silently returned IsFullAccess = false for every role,
/// including a genuine Full System Access one -- unlike RoleManagementService.ToDtoAsync, which already
/// threaded it through correctly. Live authorization was never affected (the Step 3 handlers read the Role
/// entity directly, never this DTO); this is a read-path/API-response defect only.
/// </summary>
public sealed class UserManagementServiceRoleDtoTests
{
    private readonly Mock<IUserAccessRepository> _repository = new();
    private readonly Mock<IPasswordHasher> _passwordHasher = new();
    private readonly Mock<ICurrentUserService> _currentUser = new();
    private readonly Mock<IEmailSender> _email = new();
    private readonly Mock<IExternalTokenValidator> _externalTokenValidator = new();
    private readonly Mock<ILocalAuthService> _localAuth = new();

    public UserManagementServiceRoleDtoTests()
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

    private static Role MakeRole(bool isFullAccess, string name = "Healthcare Platform Admin", bool isSystem = false)
    {
        var role = new Role(Guid.NewGuid(), name, $"{name} role.", isSystem: isSystem);
        if (isFullAccess)
        {
            role.SetFullAccess(true);
        }

        return role;
    }

    private void SetupUserWithRole(User user, Role role)
    {
        _repository.Setup(x => x.GetUserByIdAsync(user.Id, It.IsAny<CancellationToken>())).ReturnsAsync(user);
        _repository.Setup(x => x.GetUserRolesAsync(user.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<Role>)[role]);
        _repository.Setup(x => x.GetRolePermissionsAsync(role.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<Permission>)[]);
        _repository.Setup(x => x.GetUserPermissionAllocationsAsync(user.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<(Permission Permission, bool IsEnabled)>)[]);
    }

    // ── GET /users/{id}/roles ────────────────────────────────────────────────────────────
    [Fact]
    public async Task GetUserRolesAsync_returns_IsFullAccess_true_for_a_full_access_role()
    {
        var user = new User("ext-1", "user@test.local", "Test User", Guid.NewGuid());
        var role = MakeRole(isFullAccess: true);
        SetupUserWithRole(user, role);

        var result = await Service().GetUserRolesAsync(user.Id, CancellationToken.None);

        result.Should().ContainSingle();
        result[0].IsFullAccess.Should().BeTrue();
    }

    [Fact]
    public async Task GetUserRolesAsync_returns_IsFullAccess_false_for_a_normal_role()
    {
        var user = new User("ext-2", "user2@test.local", "Test User 2", Guid.NewGuid());
        var role = MakeRole(isFullAccess: false, name: "Epic Integration Manager");
        SetupUserWithRole(user, role);

        var result = await Service().GetUserRolesAsync(user.Id, CancellationToken.None);

        result.Should().ContainSingle();
        result[0].IsFullAccess.Should().BeFalse();
    }

    [Fact]
    public async Task GetUserRolesAsync_leaves_every_other_RoleDto_field_unchanged()
    {
        var user = new User("ext-3", "user3@test.local", "Test User 3", Guid.NewGuid());
        var role = MakeRole(isFullAccess: true, name: "Healthcare Platform Admin", isSystem: true);
        SetupUserWithRole(user, role);

        var result = await Service().GetUserRolesAsync(user.Id, CancellationToken.None);

        var dto = result.Should().ContainSingle().Which;
        dto.Id.Should().Be(role.Id);
        dto.Name.Should().Be("Healthcare Platform Admin");
        dto.Description.Should().Be("Healthcare Platform Admin role.");
        dto.IsSystemRole.Should().BeTrue();
        dto.Permissions.Should().BeEmpty();
    }

    // ── GET /users/{id} ──────────────────────────────────────────────────────────────────
    [Fact]
    public async Task GetUserByIdAsync_returns_IsFullAccess_true_for_a_full_access_role()
    {
        var user = new User("ext-4", "user4@test.local", "Test User 4", Guid.NewGuid());
        var role = MakeRole(isFullAccess: true);
        SetupUserWithRole(user, role);

        var result = await Service().GetUserByIdAsync(user.Id, CancellationToken.None);

        result.Roles.Should().ContainSingle();
        result.Roles[0].IsFullAccess.Should().BeTrue();
    }

    [Fact]
    public async Task GetUserByIdAsync_returns_IsFullAccess_false_for_a_normal_role()
    {
        var user = new User("ext-5", "user5@test.local", "Test User 5", Guid.NewGuid());
        var role = MakeRole(isFullAccess: false, name: "Epic Integration Manager");
        SetupUserWithRole(user, role);

        var result = await Service().GetUserByIdAsync(user.Id, CancellationToken.None);

        result.Roles.Should().ContainSingle();
        result.Roles[0].IsFullAccess.Should().BeFalse();
    }

    [Fact]
    public async Task GetUserByIdAsync_leaves_every_other_RoleDto_field_and_user_field_unchanged()
    {
        var user = new User("ext-6", "user6@test.local", "Test User 6", Guid.NewGuid());
        var role = MakeRole(isFullAccess: true, name: "Healthcare Platform Admin", isSystem: true);
        SetupUserWithRole(user, role);

        var result = await Service().GetUserByIdAsync(user.Id, CancellationToken.None);

        result.Id.Should().Be(user.Id);
        result.Email.Should().Be("user6@test.local");

        var dto = result.Roles.Should().ContainSingle().Which;
        dto.Id.Should().Be(role.Id);
        dto.Name.Should().Be("Healthcare Platform Admin");
        dto.Description.Should().Be("Healthcare Platform Admin role.");
        dto.IsSystemRole.Should().BeTrue();
        dto.Permissions.Should().BeEmpty();
    }
}

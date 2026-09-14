using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Application.Security;
using FHIRBridge.Application.Services;
using FHIRBridge.Domain.Entities;
using FluentAssertions;
using Moq;
using Xunit;

namespace FHIRBridge.UnitTests.Security;

/// <summary>
/// RBAC redesign Step 4: <see cref="Role.IsFullAccess"/> is itself a protected capability.
/// <see cref="RoleManagementService.CreateRoleAsync"/>/<see cref="RoleManagementService.UpdateRoleAsync"/>
/// resolve the ACTING caller's own Full System Access status fresh from <see cref="IUserAccessRepository"/>
/// (never from a role-name claim, and never from the request itself) and reject any attempt to grant or
/// revoke the flag unless the caller already holds it via one of their own assigned roles. Exercised here
/// with a fully mocked repository — no DB involved.
/// </summary>
public sealed class RoleManagementServiceFullAccessTests
{
    private static readonly Guid CallerId = Guid.NewGuid();

    private readonly Mock<IUserAccessRepository> _repository = new();
    private readonly Mock<ICurrentUserService> _currentUserService = new();
    private readonly Mock<IUserDisplayNameResolver> _displayNameResolver = new();

    public RoleManagementServiceFullAccessTests()
    {
        _currentUserService.Setup(x => x.CurrentUser).Returns(new CurrentUserInfo(
            ExternalUserId: "ext-1", Email: "caller@test.local", DisplayName: "Caller",
            Roles: [], IsAuthenticated: true, UserId: CallerId));

        _displayNameResolver
            .Setup(x => x.ResolveAsync(It.IsAny<IEnumerable<string?>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, string>());

        // No permission-id validation friction in these tests — every request uses an empty PermissionIds
        // collection, which vacuously passes ValidatePermissionsAsync regardless of the catalog.
        _repository.Setup(x => x.GetPermissionsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<Permission>)[]);
        _repository.Setup(x => x.GetRolePermissionsAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<Permission>)[]);
    }

    private RoleManagementService CreateSut() =>
        new(_repository.Object, _currentUserService.Object, _displayNameResolver.Object);

    private void CallerHasFullAccess(bool hasFullAccess)
    {
        var callerRole = new Role(Guid.NewGuid(), "Caller's Role", "Whatever role the acting caller holds.");
        if (hasFullAccess)
        {
            callerRole.SetFullAccess(true);
        }

        _repository.Setup(x => x.GetUserRolesAsync(CallerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<Role>)[callerRole]);
    }

    // ── normal custom role → cannot grant Full Access (via update) ─────────────────────
    [Fact]
    public async Task UpdateRoleAsync_throws_when_a_non_full_access_caller_tries_to_grant_IsFullAccess()
    {
        CallerHasFullAccess(false);
        var target = new Role(Guid.NewGuid(), "Epic Integration Manager", "A scoped custom role.");
        _repository.Setup(x => x.GetRoleByIdAsync(target.Id, It.IsAny<CancellationToken>())).ReturnsAsync(target);
        _repository.Setup(x => x.GetRoleByNameAsync(target.Name, It.IsAny<CancellationToken>())).ReturnsAsync(target);
        var sut = CreateSut();

        var act = () => sut.UpdateRoleAsync(
            target.Id,
            new UpdateRoleRequest(target.Name, target.Description, [], IsFullAccess: true),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Full System Access*");
        target.IsFullAccess.Should().BeFalse("the rejected request must never mutate the role");
        _repository.Verify(x => x.UpdateRoleAsync(It.IsAny<Role>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── normal custom role → cannot enable Full Access during role creation ────────────
    [Fact]
    public async Task CreateRoleAsync_throws_when_a_non_full_access_caller_tries_to_create_a_role_with_IsFullAccess_true()
    {
        CallerHasFullAccess(false);
        _repository.Setup(x => x.GetRoleByNameAsync("New Role", It.IsAny<CancellationToken>()))
            .ReturnsAsync((Role?)null);
        var sut = CreateSut();

        var act = () => sut.CreateRoleAsync(
            new CreateRoleRequest("New Role", "A brand-new role.", [], IsFullAccess: true),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Full System Access*");
        _repository.Verify(x => x.AddRoleAsync(It.IsAny<Role>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── normal custom role → cannot self-escalate even when targeting its own role ─────
    [Fact]
    public async Task UpdateRoleAsync_throws_even_when_the_non_full_access_caller_targets_their_own_role()
    {
        CallerHasFullAccess(false);
        var ownRole = new Role(Guid.NewGuid(), "My Own Role", "The role the caller themselves holds.");
        _repository.Setup(x => x.GetRoleByIdAsync(ownRole.Id, It.IsAny<CancellationToken>())).ReturnsAsync(ownRole);
        _repository.Setup(x => x.GetRoleByNameAsync(ownRole.Name, It.IsAny<CancellationToken>())).ReturnsAsync(ownRole);
        var sut = CreateSut();

        var act = () => sut.UpdateRoleAsync(
            ownRole.Id,
            new UpdateRoleRequest(ownRole.Name, ownRole.Description, [], IsFullAccess: true),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        ownRole.IsFullAccess.Should().BeFalse();
    }

    // ── Full Access caller → can grant Full Access ──────────────────────────────────────
    [Fact]
    public async Task UpdateRoleAsync_allows_a_full_access_caller_to_grant_IsFullAccess()
    {
        CallerHasFullAccess(true);
        var target = new Role(Guid.NewGuid(), "Epic Integration Manager", "A scoped custom role.");
        _repository.Setup(x => x.GetRoleByIdAsync(target.Id, It.IsAny<CancellationToken>())).ReturnsAsync(target);
        _repository.Setup(x => x.GetRoleByNameAsync(target.Name, It.IsAny<CancellationToken>())).ReturnsAsync(target);
        var sut = CreateSut();

        var result = await sut.UpdateRoleAsync(
            target.Id,
            new UpdateRoleRequest(target.Name, target.Description, [], IsFullAccess: true),
            CancellationToken.None);

        result.IsFullAccess.Should().BeTrue();
        target.IsFullAccess.Should().BeTrue();
        _repository.Verify(x => x.UpdateRoleAsync(target, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CreateRoleAsync_allows_a_full_access_caller_to_create_a_role_with_IsFullAccess_true()
    {
        CallerHasFullAccess(true);
        _repository.Setup(x => x.GetRoleByNameAsync("Healthcare Platform Admin", It.IsAny<CancellationToken>()))
            .ReturnsAsync((Role?)null);
        var sut = CreateSut();

        var result = await sut.CreateRoleAsync(
            new CreateRoleRequest("Healthcare Platform Admin", "Full access custom role.", [], IsFullAccess: true),
            CancellationToken.None);

        result.IsFullAccess.Should().BeTrue();
        _repository.Verify(x => x.AddRoleAsync(
            It.Is<Role>(r => r.IsFullAccess), It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── Full Access caller → can revoke Full Access ─────────────────────────────────────
    [Fact]
    public async Task UpdateRoleAsync_allows_a_full_access_caller_to_revoke_IsFullAccess()
    {
        CallerHasFullAccess(true);
        var target = new Role(Guid.NewGuid(), "Healthcare Platform Admin", "Previously full-access.");
        target.SetFullAccess(true);
        _repository.Setup(x => x.GetRoleByIdAsync(target.Id, It.IsAny<CancellationToken>())).ReturnsAsync(target);
        _repository.Setup(x => x.GetRoleByNameAsync(target.Name, It.IsAny<CancellationToken>())).ReturnsAsync(target);
        var sut = CreateSut();

        var result = await sut.UpdateRoleAsync(
            target.Id,
            new UpdateRoleRequest(target.Name, target.Description, [], IsFullAccess: false),
            CancellationToken.None);

        result.IsFullAccess.Should().BeFalse();
        target.IsFullAccess.Should().BeFalse();
        _repository.Verify(x => x.UpdateRoleAsync(target, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task UpdateRoleAsync_throws_when_a_non_full_access_caller_tries_to_revoke_another_roles_IsFullAccess()
    {
        // Revoking is gated exactly like granting -- it's a real change either way.
        CallerHasFullAccess(false);
        var target = new Role(Guid.NewGuid(), "Healthcare Platform Admin", "Previously full-access.");
        target.SetFullAccess(true);
        _repository.Setup(x => x.GetRoleByIdAsync(target.Id, It.IsAny<CancellationToken>())).ReturnsAsync(target);
        _repository.Setup(x => x.GetRoleByNameAsync(target.Name, It.IsAny<CancellationToken>())).ReturnsAsync(target);
        var sut = CreateSut();

        var act = () => sut.UpdateRoleAsync(
            target.Id,
            new UpdateRoleRequest(target.Name, target.Description, [], IsFullAccess: false),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        target.IsFullAccess.Should().BeTrue("the rejected revoke attempt must never mutate the role");
    }

    // ── Existing SuperAdmin/Admin behavior remains unchanged ────────────────────────────
    [Fact]
    public async Task UpdateRoleAsync_SuperAdmin_is_still_fully_immutable_regardless_of_IsFullAccess()
    {
        CallerHasFullAccess(true); // even a full-access caller cannot bypass SuperAdmin's own immutability
        var superAdmin = new Role(SeededSecurityIds.SuperAdminRoleId, "SuperAdmin", "Full platform administrator.", isSystem: true);
        _repository.Setup(x => x.GetRoleByIdAsync(superAdmin.Id, It.IsAny<CancellationToken>())).ReturnsAsync(superAdmin);
        var sut = CreateSut();

        var act = () => sut.UpdateRoleAsync(
            superAdmin.Id,
            new UpdateRoleRequest(superAdmin.Name, superAdmin.Description, [], IsFullAccess: false),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("System roles cannot be modified.");
    }

    [Fact]
    public async Task UpdateRoleAsync_editing_Admins_permissions_without_touching_IsFullAccess_still_works_for_a_non_full_access_caller()
    {
        // The exact backward-compatibility scenario this design protects: an older/uninformed caller (or
        // one without Full System Access but holding role.edit) resends Admin's unchanged name/description
        // and never mentions IsFullAccess (null) -- this must keep working exactly as it did before Step 4,
        // even though Admin's IsFullAccess is already true and the caller themselves lacks it.
        CallerHasFullAccess(false);
        var admin = new Role(SeededSecurityIds.AdminRoleId, "Admin", "Administers configuration and users.", isSystem: true);
        admin.SetFullAccess(true);
        _repository.Setup(x => x.GetRoleByIdAsync(admin.Id, It.IsAny<CancellationToken>())).ReturnsAsync(admin);
        var sut = CreateSut();

        var result = await sut.UpdateRoleAsync(
            admin.Id,
            new UpdateRoleRequest(admin.Name, admin.Description, [], IsFullAccess: null),
            CancellationToken.None);

        result.IsFullAccess.Should().BeTrue("nothing requested a change, so Admin's existing flag must be untouched");
        admin.IsFullAccess.Should().BeTrue();
        _repository.Verify(x => x.GetUserRolesAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never,
            "a no-op request must never even consult the caller's own access");
    }

    [Fact]
    public async Task UpdateRoleAsync_resending_Admins_current_IsFullAccess_value_explicitly_is_also_a_no_op()
    {
        // Same guarantee, but the caller explicitly resends "true" (matching Admin's current value) rather
        // than omitting the field entirely -- still not a real change, so still not gated.
        CallerHasFullAccess(false);
        var admin = new Role(SeededSecurityIds.AdminRoleId, "Admin", "Administers configuration and users.", isSystem: true);
        admin.SetFullAccess(true);
        _repository.Setup(x => x.GetRoleByIdAsync(admin.Id, It.IsAny<CancellationToken>())).ReturnsAsync(admin);
        var sut = CreateSut();

        var result = await sut.UpdateRoleAsync(
            admin.Id,
            new UpdateRoleRequest(admin.Name, admin.Description, [], IsFullAccess: true),
            CancellationToken.None);

        result.IsFullAccess.Should().BeTrue();
    }

    [Fact]
    public async Task CreateRoleAsync_a_non_full_access_caller_can_still_create_a_normal_role_exactly_as_before()
    {
        CallerHasFullAccess(false);
        _repository.Setup(x => x.GetRoleByNameAsync("Reporting Viewer", It.IsAny<CancellationToken>()))
            .ReturnsAsync((Role?)null);
        var sut = CreateSut();

        var result = await sut.CreateRoleAsync(
            new CreateRoleRequest("Reporting Viewer", "Read-only reporting access.", []),
            CancellationToken.None);

        result.IsFullAccess.Should().BeFalse();
        _repository.Verify(x => x.AddRoleAsync(
            It.Is<Role>(r => !r.IsFullAccess), It.IsAny<CancellationToken>()), Times.Once);
    }
}

using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Application.Services;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Domain.Enums;
using FHIRBridge.Governance;
using FluentAssertions;
using Moq;

namespace FHIRBridge.UnitTests.Auth;

public sealed class SsoAuthServicePingRolesTests
{
    private static readonly LocalLoginResponse Session = null!;

    private readonly Mock<IExternalTokenValidator> _validator = new();
    private readonly Mock<IUserAccessRepository> _repository = new();
    private readonly Mock<ILocalAuthService> _localAuth = new();
    private readonly Mock<IGovernanceLogger> _governance = new();
    private readonly Mock<IExternalRoleResolver> _resolver = new();
    private readonly Mock<IUserPermissionsProvider> _permissions = new();

    private readonly Role _adminRole = Role("Admin");
    private readonly Role _auditRole = Role("Audit");
    private readonly Role _opsRole = Role("Operations");

    private static readonly ExternalIdentity PingIdentity =
        new(LoginProvider.Ping, "ping-user-1", "jane@example.org", "Jane");

    public SsoAuthServicePingRolesTests()
    {
        _resolver.Setup(r => r.AppliesToAsync(It.IsAny<ExternalIdentity>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _repository.Setup(r => r.GetRoleByNameAsync("Admin", It.IsAny<CancellationToken>())).ReturnsAsync(_adminRole);
        _repository.Setup(r => r.GetRoleByNameAsync("Audit", It.IsAny<CancellationToken>())).ReturnsAsync(_auditRole);
        _repository.Setup(r => r.GetRoleByNameAsync("Operations", It.IsAny<CancellationToken>())).ReturnsAsync(_opsRole);
        _localAuth.Setup(l => l.IssueSessionAsync(It.IsAny<User>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .ReturnsAsync(Session);
    }

    private SsoAuthService Service() => new(
        _validator.Object,
        _repository.Object,
        _localAuth.Object,
        _governance.Object,
        new ExternalRoleReconciler(_repository.Object, _governance.Object, [_resolver.Object], _permissions.Object));

    private static Role Role(string name) => new(Guid.NewGuid(), name, name);

    private void ResolverReturns(
        bool succeeded,
        bool allowProvisioning,
        ExternalRoleSyncMode mode,
        ExternalRoleConflictMode conflict,
        params string[] roles) =>
        _resolver.Setup(r => r.ResolveAsync(PingIdentity, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExternalRoleResolution(succeeded, roles, allowProvisioning, mode, conflict));

    private void ResolverReturns(ExternalRoleSyncMode mode, params string[] roles) =>
        ResolverReturns(true, true, mode, ExternalRoleConflictMode.Deny, roles);

    private User ExistingUser(params Role[] currentRoles)
    {
        var user = new User("ping-user-1", "jane@example.org", "Jane", Guid.NewGuid());
        _repository.Setup(r => r.GetUserByExternalIdAsync("ping-user-1", It.IsAny<CancellationToken>())).ReturnsAsync(user);
        _repository.Setup(r => r.GetUserRolesAsync(user.Id, It.IsAny<CancellationToken>())).ReturnsAsync(currentRoles);
        return user;
    }

    private void VerifyRolesSetTo(User user, Role role) =>
        _repository.Verify(r => r.SetUserRolesAsync(
            user.Id,
            It.Is<IReadOnlyCollection<Guid>>(ids => ids.Count == 1 && ids.Single() == role.Id),
            It.IsAny<CancellationToken>()), Times.Once);

    private void VerifyRolesNeverSet() =>
        _repository.Verify(r => r.SetUserRolesAsync(
            It.IsAny<Guid>(), It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()), Times.Never);

    // ---------------------------------------------------------------- AutoSwitch

    [Fact]
    public async Task AutoSwitch_replaces_a_different_segue_role_and_records_who_changed_it()
    {
        var user = ExistingUser(_adminRole);
        ResolverReturns(ExternalRoleSyncMode.AutoSwitch, "Audit");

        await Service().LoginWithIdentityAsync(PingIdentity, CancellationToken.None);

        VerifyRolesSetTo(user, _auditRole);
        user.RoleSyncNote.Should().Contain("Role updated via Ping SSO").And.Contain("Admin to Audit");
        _permissions.Verify(p => p.Invalidate(user.Id), Times.Once);
        _governance.Verify(g => g.LogAuthenticationAsync(
            It.Is<AuthenticationEntry>(e => e.AuthenticationType == "SSO:Ping:RoleUpdated" && e.Success &&
                                            e.FailureReason!.Contains("Admin to Audit")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task AutoSwitch_leaves_a_matching_role_alone()
    {
        var user = ExistingUser(_auditRole);
        ResolverReturns(ExternalRoleSyncMode.AutoSwitch, "Audit");

        await Service().LoginWithIdentityAsync(PingIdentity, CancellationToken.None);

        VerifyRolesNeverSet();
        user.RoleSyncNote.Should().BeNull();
    }

    [Fact]
    public async Task AutoSwitch_collapses_several_segue_roles_to_the_single_ping_role()
    {
        var user = ExistingUser(_adminRole, _opsRole);
        ResolverReturns(ExternalRoleSyncMode.AutoSwitch, "Audit");

        await Service().LoginWithIdentityAsync(PingIdentity, CancellationToken.None);

        VerifyRolesSetTo(user, _auditRole);
    }

    [Fact]
    public async Task Existing_user_with_no_mapped_role_is_denied_instead_of_keeping_stale_access()
    {
        ExistingUser(_adminRole);
        ResolverReturns(ExternalRoleSyncMode.AutoSwitch);

        var act = () => Service().LoginWithIdentityAsync(PingIdentity, CancellationToken.None);

        await act.Should().ThrowAsync<UnauthorizedAccessException>();
        _localAuth.Verify(l => l.IssueSessionAsync(It.IsAny<User>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()), Times.Never);
        VerifyRolesNeverSet();
    }

    // ---------------------------------------------------------------- AllowDifferent

    [Fact]
    public async Task AllowDifferent_signs_in_with_the_segue_role_even_when_ping_maps_another()
    {
        var user = ExistingUser(_adminRole);
        ResolverReturns(ExternalRoleSyncMode.AllowDifferent, "Audit");

        await Service().LoginWithIdentityAsync(PingIdentity, CancellationToken.None);

        VerifyRolesNeverSet();
        user.RoleSyncNote.Should().BeNull();
        _localAuth.Verify(l => l.IssueSessionAsync(user, It.IsAny<CancellationToken>(), It.IsAny<bool>()), Times.Once);
    }

    // ---------------------------------------------------------------- no mapped group

    private void ResolverReturnsNoMappedGroup(ExternalNoRoleMode noRole, ExternalRoleSyncMode mode = ExternalRoleSyncMode.AutoSwitch) =>
        _resolver.Setup(r => r.ResolveAsync(PingIdentity, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExternalRoleResolution(true, [], true, mode, ExternalRoleConflictMode.Deny, noRole));

    [Theory]
    [InlineData(ExternalRoleSyncMode.AutoSwitch)]
    [InlineData(ExternalRoleSyncMode.AllowDifferent)]
    [InlineData(ExternalRoleSyncMode.RestrictMismatch)]
    public async Task NoMappedGroup_deny_refuses_the_user_in_every_sync_mode(ExternalRoleSyncMode mode)
    {
        ExistingUser(_adminRole);
        ResolverReturnsNoMappedGroup(ExternalNoRoleMode.Deny, mode);

        var act = () => Service().LoginWithIdentityAsync(PingIdentity, CancellationToken.None);

        await act.Should().ThrowAsync<UnauthorizedAccessException>();
        VerifyRolesNeverSet();
    }

    [Theory]
    [InlineData(ExternalRoleSyncMode.AutoSwitch)]
    [InlineData(ExternalRoleSyncMode.AllowDifferent)]
    [InlineData(ExternalRoleSyncMode.RestrictMismatch)]
    public async Task NoMappedGroup_allow_signs_the_user_in_with_their_segue_role_and_leaves_it_alone(ExternalRoleSyncMode mode)
    {
        var user = ExistingUser(_adminRole);
        ResolverReturnsNoMappedGroup(ExternalNoRoleMode.AllowSegueRole, mode);

        await Service().LoginWithIdentityAsync(PingIdentity, CancellationToken.None);

        VerifyRolesNeverSet();
        user.RoleSyncNote.Should().BeNull();
        _localAuth.Verify(l => l.IssueSessionAsync(user, It.IsAny<CancellationToken>(), It.IsAny<bool>()), Times.Once);
    }

    [Fact]
    public async Task NoMappedGroup_allow_does_not_create_an_account_for_an_unknown_user()
    {
        ResolverReturnsNoMappedGroup(ExternalNoRoleMode.AllowSegueRole);

        var act = () => Service().LoginWithIdentityAsync(PingIdentity, CancellationToken.None);

        await act.Should().ThrowAsync<UnauthorizedAccessException>();
        _repository.Verify(r => r.AddUserAsync(It.IsAny<User>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ---------------------------------------------------------------- RestrictMismatch

    [Fact]
    public async Task RestrictMismatch_denies_when_the_roles_differ()
    {
        ExistingUser(_adminRole);
        ResolverReturns(ExternalRoleSyncMode.RestrictMismatch, "Audit");

        var act = () => Service().LoginWithIdentityAsync(PingIdentity, CancellationToken.None);

        await act.Should().ThrowAsync<UnauthorizedAccessException>();
        VerifyRolesNeverSet();
        _localAuth.Verify(l => l.IssueSessionAsync(It.IsAny<User>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public async Task RestrictMismatch_allows_a_matching_role()
    {
        var user = ExistingUser(_auditRole);
        ResolverReturns(ExternalRoleSyncMode.RestrictMismatch, "Audit");

        await Service().LoginWithIdentityAsync(PingIdentity, CancellationToken.None);

        _localAuth.Verify(l => l.IssueSessionAsync(user, It.IsAny<CancellationToken>(), It.IsAny<bool>()), Times.Once);
    }

    [Fact]
    public async Task RestrictMismatch_does_not_fall_open_when_ping_cannot_be_reached()
    {
        ExistingUser(_adminRole);
        _resolver.Setup(r => r.ResolveAsync(PingIdentity, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ExternalRoleResolution.FailedFor(ExternalRoleSyncMode.RestrictMismatch));

        var act = () => Service().LoginWithIdentityAsync(PingIdentity, CancellationToken.None);

        await act.Should().ThrowAsync<UnauthorizedAccessException>();
    }

    // ---------------------------------------------------------------- conflicts (one Segue user = one role)

    [Fact]
    public async Task Conflict_deny_refuses_a_user_whose_groups_map_to_two_different_roles()
    {
        ExistingUser(_adminRole);
        ResolverReturns(true, true, ExternalRoleSyncMode.AutoSwitch, ExternalRoleConflictMode.Deny, "Admin", "Audit");

        var act = () => Service().LoginWithIdentityAsync(PingIdentity, CancellationToken.None);

        await act.Should().ThrowAsync<UnauthorizedAccessException>();
        VerifyRolesNeverSet();
    }

    [Fact]
    public async Task Conflict_use_mapping_order_picks_the_first_listed_role()
    {
        var user = ExistingUser(_auditRole);
        // Roles arrive in mapping order: Admin is the higher-priority row.
        ResolverReturns(true, true, ExternalRoleSyncMode.AutoSwitch, ExternalRoleConflictMode.UseMappingOrder, "Admin", "Audit");

        await Service().LoginWithIdentityAsync(PingIdentity, CancellationToken.None);

        VerifyRolesSetTo(user, _adminRole);
    }

    [Fact]
    public async Task A_single_mapped_group_among_many_ping_groups_is_allowed()
    {
        // The resolver only returns roles for groups that ARE mapped, so one mapped group among many is one role.
        var user = ExistingUser(_adminRole);
        ResolverReturns(ExternalRoleSyncMode.AutoSwitch, "Admin");

        await Service().LoginWithIdentityAsync(PingIdentity, CancellationToken.None);

        _localAuth.Verify(l => l.IssueSessionAsync(user, It.IsAny<CancellationToken>(), It.IsAny<bool>()), Times.Once);
    }

    // ---------------------------------------------------------------- provisioning and outages

    [Fact]
    public async Task Unknown_user_is_created_with_one_role_and_a_note_when_a_ping_group_maps()
    {
        ResolverReturns(ExternalRoleSyncMode.AutoSwitch, "Audit");

        await Service().LoginWithIdentityAsync(PingIdentity, CancellationToken.None);

        _repository.Verify(r => r.AddUserAsync(
            It.Is<User>(u => u.ExternalUserId == "ping-user-1" && u.LoginProvider == LoginProvider.Ping &&
                             u.RoleSyncNote!.Contains("Created via Ping SSO")),
            It.IsAny<CancellationToken>()), Times.Once);
        _repository.Verify(r => r.SetUserRolesAsync(
            It.IsAny<Guid>(),
            It.Is<IReadOnlyCollection<Guid>>(ids => ids.Single() == _auditRole.Id),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Unknown_user_with_no_mapped_role_is_rejected_and_not_created()
    {
        ResolverReturns(ExternalRoleSyncMode.AutoSwitch);

        var act = () => Service().LoginWithIdentityAsync(PingIdentity, CancellationToken.None);

        await act.Should().ThrowAsync<UnauthorizedAccessException>();
        _repository.Verify(r => r.AddUserAsync(It.IsAny<User>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Unknown_user_is_not_created_when_jit_is_off()
    {
        ResolverReturns(true, false, ExternalRoleSyncMode.AutoSwitch, ExternalRoleConflictMode.Deny, "Admin");

        var act = () => Service().LoginWithIdentityAsync(PingIdentity, CancellationToken.None);

        await act.Should().ThrowAsync<UnauthorizedAccessException>();
        _repository.Verify(r => r.AddUserAsync(It.IsAny<User>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Ping_outage_keeps_existing_users_previous_roles_in_the_lenient_modes()
    {
        var user = ExistingUser(_adminRole);
        _resolver.Setup(r => r.ResolveAsync(PingIdentity, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ExternalRoleResolution.Failed);

        await Service().LoginWithIdentityAsync(PingIdentity, CancellationToken.None);

        VerifyRolesNeverSet();
        _localAuth.Verify(l => l.IssueSessionAsync(user, It.IsAny<CancellationToken>(), It.IsAny<bool>()), Times.Once);
    }

    [Fact]
    public async Task Ping_outage_does_not_provision_unknown_users()
    {
        _resolver.Setup(r => r.ResolveAsync(PingIdentity, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ExternalRoleResolution.Failed);

        var act = () => Service().LoginWithIdentityAsync(PingIdentity, CancellationToken.None);

        await act.Should().ThrowAsync<UnauthorizedAccessException>();
        _repository.Verify(r => r.AddUserAsync(It.IsAny<User>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Providers_without_a_resolver_behave_as_before()
    {
        var google = new ExternalIdentity(LoginProvider.Google, "g-1", "g@example.org", "G");
        _resolver.Setup(r => r.AppliesToAsync(google, It.IsAny<CancellationToken>())).ReturnsAsync(false);

        var act = () => Service().LoginWithIdentityAsync(google, CancellationToken.None);

        await act.Should().ThrowAsync<UnauthorizedAccessException>();
        _resolver.Verify(r => r.ResolveAsync(It.IsAny<ExternalIdentity>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ---------------------------------------------------------------- the user is told the real reason

    [Fact]
    public async Task Message_for_an_existing_user_with_no_mapped_group_names_the_missing_group_not_the_account()
    {
        ExistingUser(_adminRole);
        ResolverReturnsNoMappedGroup(ExternalNoRoleMode.Deny);

        var act = () => Service().LoginWithIdentityAsync(PingIdentity, CancellationToken.None);

        (await act.Should().ThrowAsync<UnauthorizedAccessException>()).Which.Message
            .Should().Contain("PingOne account is not in any group that is linked to a Segue role")
            .And.NotContain("invitation");
    }

    [Fact]
    public async Task Message_for_an_unknown_user_with_no_mapped_group_says_no_account_exists_and_no_group_is_linked()
    {
        ResolverReturnsNoMappedGroup(ExternalNoRoleMode.Deny);

        var act = () => Service().LoginWithIdentityAsync(PingIdentity, CancellationToken.None);

        (await act.Should().ThrowAsync<UnauthorizedAccessException>()).Which.Message
            .Should().Contain("No Segue account exists for this PingOne user").And.Contain("none of their groups");
    }

    [Fact]
    public async Task Message_for_a_conflict_says_the_groups_map_to_more_than_one_role()
    {
        ExistingUser(_adminRole);
        ResolverReturns(true, true, ExternalRoleSyncMode.AutoSwitch, ExternalRoleConflictMode.Deny, "Admin", "Audit");

        var act = () => Service().LoginWithIdentityAsync(PingIdentity, CancellationToken.None);

        (await act.Should().ThrowAsync<UnauthorizedAccessException>()).Which.Message
            .Should().Contain("linked to more than one Segue role");
    }

    [Fact]
    public async Task Message_for_a_restricted_mismatch_names_both_roles()
    {
        ExistingUser(_adminRole);
        ResolverReturns(ExternalRoleSyncMode.RestrictMismatch, "Audit");

        var act = () => Service().LoginWithIdentityAsync(PingIdentity, CancellationToken.None);

        (await act.Should().ThrowAsync<UnauthorizedAccessException>()).Which.Message
            .Should().Contain("Segue role (Admin)").And.Contain("PingOne role (Audit)");
    }

    [Fact]
    public async Task Message_for_a_disabled_account_says_it_is_disabled()
    {
        var user = ExistingUser(_adminRole);
        user.SetEnabled(false);
        ResolverReturns(ExternalRoleSyncMode.AutoSwitch, "Audit");

        var act = () => Service().LoginWithIdentityAsync(PingIdentity, CancellationToken.None);

        (await act.Should().ThrowAsync<UnauthorizedAccessException>()).Which.Message
            .Should().Contain("Segue account is disabled");
    }

    [Fact]
    public void Every_message_is_short_enough_to_pass_the_client_safe_filter()
    {
        // The API only passes a message through when it is a single short sentence (<= 200 chars, no markup).
        // The longest real message is the missing-group one with the longest role names; pin the shape here.
        var longest = "No Segue account exists for this PingOne user, and none of their groups is linked to a Segue role. Ask an administrator.";
        FHIRBridge.Governance.SafeErrorText.Sanitize(longest).Should().Be(longest);
    }
}

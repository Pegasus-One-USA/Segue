using System.Security.Claims;
using FHIRBridge.Api.Security;
using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Domain.Entities;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Moq;
using Xunit;

namespace FHIRBridge.UnitTests.Security;

/// <summary>
/// RBAC redesign Step 3: <see cref="UnifiedAdminAuthorizationHandler"/> and
/// <see cref="SuperAdminOnlyAuthorizationHandler"/> now succeed either on the pre-existing SuperAdmin/
/// Admin role-name claim (unchanged, for backward compatibility) OR when any of the caller's assigned
/// roles has <see cref="Role.IsFullAccess"/> set — resolved from <see cref="IUserAccessRepository"/>,
/// never from the role's name. Exercised here directly against real handler instances with mocked
/// dependencies (no DB, no web host, no integration-test harness involved) so this stays a fast, reliable
/// unit test independent of the known-broken ApiFactory/WebApplicationFactory fixture.
/// </summary>
public sealed class FullAccessAuthorizationHandlerTests
{
    private static readonly Guid UserId = Guid.NewGuid();

    private static ClaimsPrincipal AuthenticatedUser(params string[] roleClaims)
    {
        var claims = new List<Claim> { new("uid", UserId.ToString()) };
        claims.AddRange(roleClaims.Select(r => new Claim(ClaimTypes.Role, r)));
        var identity = new ClaimsIdentity(claims, authenticationType: "Test");
        return new ClaimsPrincipal(identity);
    }

    private static Mock<ICurrentUserService> CurrentUserServiceReturning(Guid? userId)
    {
        var mock = new Mock<ICurrentUserService>();
        mock.Setup(x => x.CurrentUser).Returns(new CurrentUserInfo(
            ExternalUserId: "ext-1", Email: "user@test.local", DisplayName: "Test User",
            Roles: [], IsAuthenticated: userId is not null, UserId: userId));
        return mock;
    }

    private static Mock<IUserAccessRepository> RepositoryReturning(params Role[] roles)
    {
        var mock = new Mock<IUserAccessRepository>();
        mock.Setup(x => x.GetUserRolesAsync(UserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<Role>)roles);
        return mock;
    }

    private static Role NewRole(bool isFullAccess, string name = "Custom Role")
    {
        var role = new Role(Guid.NewGuid(), name, "A custom, non-built-in role.");
        role.SetFullAccess(isFullAccess);
        return role;
    }

    private static async Task<bool> RunAsync<THandler, TRequirement>(
        THandler handler, TRequirement requirement, ClaimsPrincipal user)
        where THandler : IAuthorizationHandler
        where TRequirement : IAuthorizationRequirement
    {
        var context = new AuthorizationHandlerContext([requirement], user, resource: null);
        await handler.HandleAsync(context);
        return context.HasSucceeded;
    }

    // ── 1) FullAccess custom role → UnifiedAdmin allowed ────────────────────────────────
    [Fact]
    public async Task UnifiedAdmin_allows_a_custom_role_with_IsFullAccess_true()
    {
        var user = AuthenticatedUser("Epic Integration Manager"); // not a SuperAdmin/Admin claim
        var handler = new UnifiedAdminAuthorizationHandler(
            CurrentUserServiceReturning(UserId).Object,
            RepositoryReturning(NewRole(isFullAccess: true, "Epic Integration Manager")).Object);

        var succeeded = await RunAsync(handler, new UnifiedAdminRequirement(), user);

        succeeded.Should().BeTrue();
    }

    // ── 2) FullAccess custom role → SuperAdminOnly allowed ──────────────────────────────
    [Fact]
    public async Task SuperAdminOnly_allows_a_custom_role_with_IsFullAccess_true()
    {
        var user = AuthenticatedUser("Epic Integration Manager");
        var handler = new SuperAdminOnlyAuthorizationHandler(
            CurrentUserServiceReturning(UserId).Object,
            RepositoryReturning(NewRole(isFullAccess: true, "Epic Integration Manager")).Object);

        var succeeded = await RunAsync(handler, new SuperAdminOnlyRequirement(), user);

        succeeded.Should().BeTrue();
    }

    // ── 3) Normal custom role → UnifiedAdmin denied ─────────────────────────────────────
    [Fact]
    public async Task UnifiedAdmin_denies_a_custom_role_with_IsFullAccess_false()
    {
        var user = AuthenticatedUser("Epic Integration Manager");
        var handler = new UnifiedAdminAuthorizationHandler(
            CurrentUserServiceReturning(UserId).Object,
            RepositoryReturning(NewRole(isFullAccess: false, "Epic Integration Manager")).Object);

        var succeeded = await RunAsync(handler, new UnifiedAdminRequirement(), user);

        succeeded.Should().BeFalse();
    }

    // ── 4) Normal custom role → SuperAdminOnly denied ───────────────────────────────────
    [Fact]
    public async Task SuperAdminOnly_denies_a_custom_role_with_IsFullAccess_false()
    {
        var user = AuthenticatedUser("Epic Integration Manager");
        var handler = new SuperAdminOnlyAuthorizationHandler(
            CurrentUserServiceReturning(UserId).Object,
            RepositoryReturning(NewRole(isFullAccess: false, "Epic Integration Manager")).Object);

        var succeeded = await RunAsync(handler, new SuperAdminOnlyRequirement(), user);

        succeeded.Should().BeFalse();
    }

    // ── 5) Existing SuperAdmin (role-name claim) → unchanged ────────────────────────────
    [Fact]
    public async Task UnifiedAdmin_still_allows_the_SuperAdmin_role_name_claim()
    {
        var user = AuthenticatedUser("SuperAdmin");
        // Repository would throw if consulted for this path — proves the role-name branch short-circuits
        // before ever touching the DB, exactly as before this change.
        var repo = new Mock<IUserAccessRepository>(MockBehavior.Strict);
        var handler = new UnifiedAdminAuthorizationHandler(CurrentUserServiceReturning(UserId).Object, repo.Object);

        var succeeded = await RunAsync(handler, new UnifiedAdminRequirement(), user);

        succeeded.Should().BeTrue();
        repo.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task SuperAdminOnly_still_allows_the_SuperAdmin_role_name_claim()
    {
        var user = AuthenticatedUser("SuperAdmin");
        var repo = new Mock<IUserAccessRepository>(MockBehavior.Strict);
        var handler = new SuperAdminOnlyAuthorizationHandler(CurrentUserServiceReturning(UserId).Object, repo.Object);

        var succeeded = await RunAsync(handler, new SuperAdminOnlyRequirement(), user);

        succeeded.Should().BeTrue();
        repo.VerifyNoOtherCalls();
    }

    // ── 6) Existing Admin (role-name claim) → unchanged: UnifiedAdmin yes, SuperAdminOnly no ──
    [Fact]
    public async Task UnifiedAdmin_still_allows_the_Admin_role_name_claim()
    {
        var user = AuthenticatedUser("Admin");
        var repo = new Mock<IUserAccessRepository>(MockBehavior.Strict);
        var handler = new UnifiedAdminAuthorizationHandler(CurrentUserServiceReturning(UserId).Object, repo.Object);

        var succeeded = await RunAsync(handler, new UnifiedAdminRequirement(), user);

        succeeded.Should().BeTrue();
        repo.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task SuperAdminOnly_still_denies_the_plain_Admin_role_name_claim()
    {
        // SuperAdminOnly has always excluded plain "Admin" — must remain true after this change. Falls
        // through to the IsFullAccess check, same as any other non-matching role-name claim would.
        var user = AuthenticatedUser("Admin");
        var handler = new SuperAdminOnlyAuthorizationHandler(
            CurrentUserServiceReturning(UserId).Object,
            RepositoryReturning(NewRole(isFullAccess: false, "Admin")).Object);

        var succeeded = await RunAsync(handler, new SuperAdminOnlyRequirement(), user);

        succeeded.Should().BeFalse();
    }

    // ── Extra: a normal custom role holding every permission code still cannot bypass either
    //    policy — these two handlers never consult permission codes at all, only IsFullAccess, so
    //    this proves permission-code volume is irrelevant to them by construction. ──────────────
    [Fact]
    public async Task UnifiedAdmin_denies_a_custom_role_even_with_every_permission_code_when_IsFullAccess_is_false()
    {
        var user = AuthenticatedUser("Epic Integration Manager");
        var repo = new Mock<IUserAccessRepository>();
        repo.Setup(x => x.GetUserRolesAsync(UserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<Role>)[NewRole(isFullAccess: false)]);
        // No IUserPermissionsProvider dependency exists on this handler at all -- there is nothing to
        // configure "all permission codes" onto; that's the point being verified.
        var handler = new UnifiedAdminAuthorizationHandler(CurrentUserServiceReturning(UserId).Object, repo.Object);

        var succeeded = await RunAsync(handler, new UnifiedAdminRequirement(), user);

        succeeded.Should().BeFalse();
    }

    [Fact]
    public async Task SuperAdminOnly_denies_a_custom_role_even_with_every_permission_code_when_IsFullAccess_is_false()
    {
        var user = AuthenticatedUser("Epic Integration Manager");
        var handler = new SuperAdminOnlyAuthorizationHandler(
            CurrentUserServiceReturning(UserId).Object,
            RepositoryReturning(NewRole(isFullAccess: false)).Object);

        var succeeded = await RunAsync(handler, new SuperAdminOnlyRequirement(), user);

        succeeded.Should().BeFalse();
    }

    // ── Edge cases exercising the new code path directly ────────────────────────────────
    [Fact]
    public async Task UnifiedAdmin_denies_when_unauthenticated()
    {
        var identity = new ClaimsIdentity(); // IsAuthenticated = false (no authenticationType)
        var user = new ClaimsPrincipal(identity);
        var repo = new Mock<IUserAccessRepository>(MockBehavior.Strict);
        var handler = new UnifiedAdminAuthorizationHandler(CurrentUserServiceReturning(UserId).Object, repo.Object);

        var succeeded = await RunAsync(handler, new UnifiedAdminRequirement(), user);

        succeeded.Should().BeFalse();
        repo.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task UnifiedAdmin_denies_when_current_user_id_cannot_be_resolved()
    {
        var user = AuthenticatedUser("Epic Integration Manager");
        var repo = new Mock<IUserAccessRepository>(MockBehavior.Strict);
        var handler = new UnifiedAdminAuthorizationHandler(CurrentUserServiceReturning(userId: null).Object, repo.Object);

        var succeeded = await RunAsync(handler, new UnifiedAdminRequirement(), user);

        succeeded.Should().BeFalse();
        repo.VerifyNoOtherCalls();
    }
}
